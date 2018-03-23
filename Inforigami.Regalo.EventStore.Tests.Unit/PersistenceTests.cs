﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using EventStore.ClientAPI;
using Inforigami.Regalo.Core;
using Inforigami.Regalo.EventSourcing;
using Inforigami.Regalo.EventStore.Tests.Unit.DomainModel.Customers;
using Inforigami.Regalo.Interfaces;
using Inforigami.Regalo.Testing;
using NUnit.Framework;
using ILogger = Inforigami.Regalo.Core.ILogger;

namespace Inforigami.Regalo.EventStore.Tests.Unit
{
    [TestFixture]
    public class PersistenceTests
    {
        private IEventStoreConnection _eventStoreConnection;
        private ILogger _logger;

        [SetUp]
        public void SetUp()
        {
            _logger = new ConsoleLogger();

            _eventStoreConnection = EventStoreConnection.Create(new IPEndPoint(IPAddress.Loopback, 1113));
            _eventStoreConnection.ConnectAsync().Wait();

            Resolver.Configure(
                type =>
                {
                    if (type == typeof(ILogger))
                    {
                        return _logger;
                    }

                    throw new InvalidOperationException($"No type of {type} registered.");
                },
                type => null,
                o => { });
        }

        [TearDown]
        public void TearDown()
        {
            Conventions.SetFindAggregateTypeForEventType(null);

            Resolver.Reset();

            _eventStoreConnection.Close();
            _eventStoreConnection = null;

            _logger = null;
        }

        [Test]
        public void Loading_GivenEmptyStore_ShouldReturnNull()
        {
            // Arrange
            IEventStore store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());

            // Act
            EventStream<Customer> events = store.Load<Customer>(Guid.NewGuid().ToString());

            // Assert
            Assert.That(events, Is.Null);
        }

        [Test]
        public void Saving_GivenSingleEvent_ShouldAllowReloading()
        {
            // Arrange
            var store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());

            // Act
            var id = Guid.NewGuid();
            var evt = new CustomerSignedUp(id);
            store.Save<Customer>(id.ToString(), EventStreamVersion.NoStream, new[] { evt });
            store.Flush();
            var stream = store.Load<Customer>(id.ToString());

            // Assert
            Assert.NotNull(stream);
            CollectionAssert.AreEqual(
                new object[] { evt },
                stream.Events,
                "Events reloaded from store do not match those generated by aggregate.");
        }

        [Test]
        public void Saving_GivenEventWithGuidProperty_ShouldAllowReloadingToGuidType()
        {
            // Arrange
            var store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());

            var customer = new Customer();
            customer.Signup();

            var accountManager = new AccountManager();
            var startDate = new DateTime(2012, 4, 28);
            accountManager.Employ(startDate);

            customer.AssignAccountManager(accountManager.Id, startDate);

            store.Save<Customer>(customer.Id.ToString(), EventStreamVersion.NoStream, customer.GetUncommittedEvents());
            store.Flush();

            // Act
            var acctMgrAssignedEvent = (AccountManagerAssigned)store.Load<Customer>(customer.Id.ToString())
                                                                    .Events
                                                                    .LastOrDefault();

            // Assert
            Assert.NotNull(acctMgrAssignedEvent);
            Assert.AreEqual(accountManager.Id, acctMgrAssignedEvent.AccountManagerId);
        }

        [Test]
        public void Saving_GivenEvents_ShouldAllowReloading()
        {
            // Arrange
            var store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());

            // Act
            var customer = new Customer();
            customer.Signup();
            store.Save<Customer>(customer.Id.ToString(), EventStreamVersion.NoStream, customer.GetUncommittedEvents());
            store.Flush();
            var stream = store.Load<Customer>(customer.Id.ToString());

            // Assert
            Assert.NotNull(stream);
            CollectionAssert.AreEqual(customer.GetUncommittedEvents(), stream.Events, "Events reloaded from store do not match those generated by aggregate.");
        }

        [Test]
        public void SavingButNotCommitting_GivenEvents_DoesNotAllowReloading()
        {
            // Arrange
            IEventStore store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());

            // Act
            var customer = new Customer();
            customer.Signup();
            store.Save<Customer>(customer.Id.ToString(), EventStreamVersion.NoStream, customer.GetUncommittedEvents());
            var stream = store.Load<Customer>(customer.Id.ToString());

            // Assert
            Assert.Null(stream);
        }


        [Test]
        public void Saving_GivenNoEvents_ShouldDoNothing()
        {
            // Arrange
            IEventStore store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());

            // Act
            var id = Guid.NewGuid();
            store.Save<Customer>(id.ToString(), EventStreamVersion.NoStream, Enumerable.Empty<IEvent>());
            var stream = store.Load<Customer>(id.ToString());

            // Assert
            Assert.That(stream, Is.Null);
        }

        [Test]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void GivenAggregateWithMultipleEvents_WhenLoadingSpecificVersion_ThenShouldOnlyReturnRequestedEvents(int version)
        {
            // Arrange
            var store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());
            var customerId = Guid.NewGuid();
            var storedEvents = new EventChain().Add(new CustomerSignedUp(customerId))
                                               .Add(new SubscribedToNewsletter("latest"))
                                               .Add(new SubscribedToNewsletter("top"));
            store.Save<Customer>(customerId.ToString(), EventStreamVersion.NoStream, storedEvents);
            store.Flush();

            // Act
            var stream = store.Load<Customer>(customerId.ToString(), version);

            // Assert
            CollectionAssert.AreEqual(storedEvents.Take(version + 1), stream.Events, "Events loaded from store do not match version requested.");
        }

        [Test]
        public void GivenAggregateWithMultipleEvents_WhenLoadingMaxVersion_ThenShouldReturnAllEvents()
        {
            // Arrange
            var store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());
            var customerId = Guid.NewGuid();
            var storedEvents = new EventChain().Add(new CustomerSignedUp(customerId))
                                               .Add(new SubscribedToNewsletter("latest"))
                                               .Add(new SubscribedToNewsletter("top"));
            store.Save<Customer>(customerId.ToString(), EventStreamVersion.NoStream, storedEvents);
            store.Flush();

            // Act
            var stream = store.Load<Customer>(customerId.ToString(), EventStreamVersion.Max);

            // Assert
            CollectionAssert.AreEqual(storedEvents, stream.Events, "Events loaded from store do not match version requested.");
        }

        [Test]
        public void GivenAggregateWithMultipleEvents_WhenLoadingSpecificVersionThatNoEventHas_ThenShouldFail()
        {
            // Arrange
            var store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());
            var customerId = Guid.NewGuid();
            var storedEvents = new IEvent[]
                              {
                                  new CustomerSignedUp(customerId), 
                                  new SubscribedToNewsletter("latest"), 
                                  new SubscribedToNewsletter("top")
                              };
            store.Save<Customer>(customerId.ToString(), EventStreamVersion.NoStream, storedEvents);
            store.Flush();

            // Act / Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Load<Customer>(customerId.ToString(), 10));
        }

        [Test]
        public void GivenAggregateWithMultipleEvents_WhenLoadingSpecialNoStreamVersion_ThenShouldFail()
        {
            // Arrange
            IEventStore store = new EventStoreEventStore(_eventStoreConnection, new ConsoleLogger());
            var customerId = Guid.NewGuid();
            var storedEvents = new IEvent[]
                              {
                                  new CustomerSignedUp(customerId), 
                                  new SubscribedToNewsletter("latest"), 
                                  new SubscribedToNewsletter("top")
                              };
            store.Save<Customer>(customerId.ToString(), EventStreamVersion.NoStream, storedEvents);

            // Act / Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Load<Customer>(customerId.ToString(), EventStreamVersion.NoStream));
        }
    }
}