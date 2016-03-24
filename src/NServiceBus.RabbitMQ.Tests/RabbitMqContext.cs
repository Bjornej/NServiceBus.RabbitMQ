﻿namespace NServiceBus.Transports.RabbitMQ.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using NServiceBus.Settings;
    using NServiceBus.Transports.RabbitMQ.Config;
    using NServiceBus.Transports.RabbitMQ.Connection;
    using NServiceBus.Transports.RabbitMQ.Routing;
    using NUnit.Framework;

    class RabbitMqContext
    {
        protected void MakeSureQueueAndExchangeExists(string queueName)
        {
            using (var connection = connectionManager.GetAdministrationConnection())
            using (var channel = connection.CreateModel())
            {
                channel.QueueDeclare(queueName, true, false, false, null);
                channel.QueuePurge(queueName);

                //to make sure we kill old subscriptions
                DeleteExchange(queueName);

                routingTopology.Initialize(channel, queueName);
            }
        }

        void DeleteExchange(string exchangeName)
        {
            using (var connection = connectionManager.GetAdministrationConnection())
            using (var channel = connection.CreateModel())
            {
                try
                {
                    channel.ExchangeDelete(exchangeName);
                }
                // ReSharper disable EmptyGeneralCatchClause
                catch (Exception)
                // ReSharper restore EmptyGeneralCatchClause
                {
                }
            }
        }

        public virtual int MaximumConcurrency => 1;

        [SetUp]
        public void SetUp()
        {
            routingTopology = new ConventionalRoutingTopology(true);
            receivedMessages = new BlockingCollection<IncomingMessage>();

            var settings = new SettingsHolder();
            settings.Set<NServiceBus.Routing.EndpointName>(new NServiceBus.Routing.EndpointName(ReceiverQueue));

            var config = new ConnectionConfiguration(settings);
            config.Host = "localhost";

            connectionFactory = new RabbitMqConnectionFactory(config);
            connectionManager = new ConnectionManager(connectionFactory);
            var channelProvider = new ChannelProvider(connectionManager, config.UsePublisherConfirms, config.MaxWaitTimeForConfirms);

            messageDispatcher = new MessageDispatcher(routingTopology, channelProvider);

            var purger = new QueuePurger(connectionManager);
            var poisonMessageForwarder = new PoisonMessageForwarder(channelProvider, routingTopology);

            messagePump = new MessagePump(config, new MessageConverter(), "Unit test", poisonMessageForwarder, purger, TimeSpan.FromMinutes(2));

            MakeSureQueueAndExchangeExists(ReceiverQueue);

            subscriptionManager = new SubscriptionManager(connectionManager, routingTopology, ReceiverQueue);

            messagePump.Init(pushContext =>
            {
                receivedMessages.Add(new IncomingMessage(pushContext.MessageId, pushContext.Headers, pushContext.BodyStream));
                return TaskEx.CompletedTask;
            },
                new CriticalError(_ => TaskEx.CompletedTask),
                new PushSettings(ReceiverQueue, "error", true, TransportTransactionMode.ReceiveOnly)
            ).GetAwaiter().GetResult();

            messagePump.Start(new PushRuntimeSettings(MaximumConcurrency));
        }

        [TearDown]
        public void TearDown()
        {
            messagePump?.Stop().GetAwaiter().GetResult();

            connectionManager?.Dispose();
        }

        protected IncomingMessage WaitForMessage()
        {
            var waitTime = TimeSpan.FromSeconds(1);

            if (Debugger.IsAttached)
            {
                waitTime = TimeSpan.FromMinutes(10);
            }

            IncomingMessage message;
            receivedMessages.TryTake(out message, waitTime);

            return message;
        }

        protected const string ReceiverQueue = "testreceiver";
        protected MessageDispatcher messageDispatcher;
        protected RabbitMqConnectionFactory connectionFactory;
        protected ConnectionManager connectionManager;
        protected MessagePump messagePump;
        BlockingCollection<IncomingMessage> receivedMessages;

        protected ConventionalRoutingTopology routingTopology;
        protected SubscriptionManager subscriptionManager;
    }
}
