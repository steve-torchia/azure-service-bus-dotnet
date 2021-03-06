﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.ServiceBus
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Core;
    using Primitives;

    sealed class MessageReceivePump
    {
        const int MaxInitialReceiveRetryCount = 3;
        static readonly TimeSpan ServerBusyExceptionBackoffAmount = TimeSpan.FromSeconds(10);
        static readonly TimeSpan OtherExceptionBackoffAmount = TimeSpan.FromSeconds(1);
        readonly Func<Message, CancellationToken, Task> onMessageCallback;
        readonly RegisterHandlerOptions registerHandlerOptions;
        readonly MessageReceiver messageReceiver;
        readonly CancellationToken pumpCancellationToken;
        readonly SemaphoreSlim maxConcurrentCallsSemaphoreSlim;

        public MessageReceivePump(
            MessageReceiver messageReceiver,
            RegisterHandlerOptions registerHandlerOptions,
            Func<Message, CancellationToken, Task> callback,
            CancellationToken pumpCancellationToken)
        {
            if (messageReceiver == null)
            {
                throw new ArgumentNullException(nameof(messageReceiver));
            }

            this.messageReceiver = messageReceiver;
            this.registerHandlerOptions = registerHandlerOptions;
            this.onMessageCallback = callback;
            this.pumpCancellationToken = pumpCancellationToken;
            this.maxConcurrentCallsSemaphoreSlim = new SemaphoreSlim(this.registerHandlerOptions.MaxConcurrentCalls);
        }

        public async Task StartPumpAsync()
        {
            int retryCount = 0;
            Message initialMessage = null;
            while (true)
            {
                try
                {
                    initialMessage = await this.messageReceiver.ReceiveAsync().ConfigureAwait(false);
                    MessagingEventSource.Log.MessageReceiverPumpInitialMessageReceived(this.messageReceiver.ClientId, initialMessage);
                    break;
                }
                catch (Exception exception)
                {
                    retryCount++;
                    MessagingEventSource.Log.MessageReceiverPumpInitialMessageReceiveException(this.messageReceiver.ClientId, retryCount, exception);

                    if (retryCount == MaxInitialReceiveRetryCount ||
                        !this.ShouldRetry(exception))
                    {
                        throw;
                    }

                    TimeSpan backOffTime = this.GetBackOffTime(exception);
                    await Task.Delay(backOffTime, this.pumpCancellationToken).ConfigureAwait(false);
                }
            }

            TaskExtensionHelper.Schedule(() => this.MessagePumpTask(initialMessage));
        }

        TimeSpan GetBackOffTime(Exception exception)
        {
            return exception is ServerBusyException ? ServerBusyExceptionBackoffAmount : OtherExceptionBackoffAmount;
        }

        bool ShouldRetry(Exception exception)
        {
            ServiceBusException serviceBusException = exception as ServiceBusException;
            return serviceBusException != null && serviceBusException.IsTransient;
        }

        bool ShouldRenewLock()
        {
            return
                this.messageReceiver.ReceiveMode == ReceiveMode.PeekLock &&
                this.registerHandlerOptions.AutoRenewLock;
        }

        async Task MessagePumpTask(Message initialMessage)
        {
            while (!this.pumpCancellationToken.IsCancellationRequested)
            {
                Message message = null;
                try
                {
                    await this.maxConcurrentCallsSemaphoreSlim.WaitAsync(this.pumpCancellationToken).ConfigureAwait(false);

                    if (initialMessage == null)
                    {
                        message = await this.messageReceiver.ReceiveAsync();
                    }
                    else
                    {
                        message = initialMessage;
                        initialMessage = null;
                    }

                    if (message != null)
                    {
                        MessagingEventSource.Log.MessageReceiverPumpTaskStart(this.messageReceiver.ClientId, message, this.maxConcurrentCallsSemaphoreSlim.CurrentCount);
                        TaskExtensionHelper.Schedule(() => this.MessageDispatchTask(message));
                    }
                }
                catch (Exception exception)
                {
                    MessagingEventSource.Log.MessageReceivePumpTaskException(this.messageReceiver.ClientId, exception);
                    this.registerHandlerOptions.RaiseExceptionReceived(new ExceptionReceivedEventArgs(exception, "Receive"));
                    TimeSpan backOffTimeSpan = this.GetBackOffTime(exception);
                    await Task.Delay(backOffTimeSpan, this.pumpCancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // Either an exception or for some reason message was null, release semaphore and retry.
                    if (message == null)
                    {
                        this.maxConcurrentCallsSemaphoreSlim.Release();
                        MessagingEventSource.Log.MessageReceiverPumpTaskStop(this.messageReceiver.ClientId, this.maxConcurrentCallsSemaphoreSlim.CurrentCount);
                    }
                }
            }
        }

        async Task MessageDispatchTask(Message message)
        {
            CancellationTokenSource renewLockCancellationTokenSource = null;
            Timer autoRenewLockCancellationTimer = null;

            MessagingEventSource.Log.MessageReceiverPumpDispatchTaskStart(this.messageReceiver.ClientId, message);

            if (this.ShouldRenewLock())
            {
                renewLockCancellationTokenSource = new CancellationTokenSource();
                TaskExtensionHelper.Schedule(() => this.RenewMessageLockTask(message, renewLockCancellationTokenSource.Token));

                // After a threshold time of renewal('AutoRenewTimeout'), create timer to cancel anymore renewals.
                autoRenewLockCancellationTimer = new Timer(this.CancelAutoRenewlock, renewLockCancellationTokenSource, this.registerHandlerOptions.AutoRenewTimeout, TimeSpan.FromMilliseconds(-1));
            }

            try
            {
                MessagingEventSource.Log.MessageReceiverPumpUserCallbackStart(this.messageReceiver.ClientId, message);
                await this.onMessageCallback(message, this.pumpCancellationToken).ConfigureAwait(false);
                MessagingEventSource.Log.MessageReceiverPumpUserCallbackStop(this.messageReceiver.ClientId, message);
            }
            catch (Exception exception)
            {
                MessagingEventSource.Log.MessageReceiverPumpUserCallbackException(this.messageReceiver.ClientId, message, exception);
                this.registerHandlerOptions.RaiseExceptionReceived(new ExceptionReceivedEventArgs(exception, "UserCallback"));

                // Nothing much to do if UserCallback throws, Abandon message and Release semaphore.
                await this.AbandonMessageIfNeededAsync(message).ConfigureAwait(false);
                this.maxConcurrentCallsSemaphoreSlim.Release();
                return;
            }
            finally
            {
                renewLockCancellationTokenSource?.Cancel();
                renewLockCancellationTokenSource?.Dispose();
                autoRenewLockCancellationTimer?.Dispose();
            }

            // If we've made it this far, user callback completed fine. Complete message and Release semaphore.
            await this.CompleteMessageIfNeededAsync(message).ConfigureAwait(false);
            this.maxConcurrentCallsSemaphoreSlim.Release();

            MessagingEventSource.Log.MessageReceiverPumpDispatchTaskStop(this.messageReceiver.ClientId, message, this.maxConcurrentCallsSemaphoreSlim.CurrentCount);
        }

        void CancelAutoRenewlock(object state)
        {
            CancellationTokenSource renewLockCancellationTokenSource = (CancellationTokenSource)state;
            try
            {
                renewLockCancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore this race.
            }
        }

        async Task AbandonMessageIfNeededAsync(Message message)
        {
            try
            {
                if (this.messageReceiver.ReceiveMode == ReceiveMode.PeekLock)
                {
                    await this.messageReceiver.AbandonAsync(message.SystemProperties.LockToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                this.registerHandlerOptions.RaiseExceptionReceived(new ExceptionReceivedEventArgs(exception, "Abandon"));
            }
        }

        async Task CompleteMessageIfNeededAsync(Message message)
        {
            try
            {
                if (this.messageReceiver.ReceiveMode == ReceiveMode.PeekLock &&
                    this.registerHandlerOptions.AutoComplete)
                {
                    await this.messageReceiver.CompleteAsync(new[] { message.SystemProperties.LockToken }).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                this.registerHandlerOptions.RaiseExceptionReceived(new ExceptionReceivedEventArgs(exception, "Complete"));
            }
        }

        async Task RenewMessageLockTask(Message message, CancellationToken renewLockCancellationToken)
        {
            while (!this.pumpCancellationToken.IsCancellationRequested &&
                   !renewLockCancellationToken.IsCancellationRequested)
            {
                try
                {
                    TimeSpan amount = MessagingUtilities.CalculateRenewAfterDuration(message.SystemProperties.LockedUntilUtc);
                    MessagingEventSource.Log.MessageReceiverPumpRenewMessageStart(this.messageReceiver.ClientId, message, amount);
                    await Task.Delay(amount, renewLockCancellationToken).ConfigureAwait(false);

                    if (!this.pumpCancellationToken.IsCancellationRequested &&
                        !renewLockCancellationToken.IsCancellationRequested)
                    {
                        await this.messageReceiver.RenewLockAsync(message.SystemProperties.LockToken).ConfigureAwait(false);
                        MessagingEventSource.Log.MessageReceiverPumpRenewMessageStop(this.messageReceiver.ClientId, message);
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception exception)
                {
                    MessagingEventSource.Log.MessageReceiverPumpRenewMessageException(this.messageReceiver.ClientId, message, exception);

                    // TaskCancelled is expected here as renewTasks will be cancelled after the Complete call is made.
                    // Lets not bother user with this exception.
                    if (exception is TaskCanceledException)
                    {
                        this.registerHandlerOptions.RaiseExceptionReceived(new ExceptionReceivedEventArgs(exception, "RenewLock"));
                    }
                    if (!this.ShouldRetry(exception))
                    {
                        break;
                    }

                    TimeSpan backoffTimeSpan = this.GetBackOffTime(exception);
                    await Task.Delay(backoffTimeSpan, this.pumpCancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}