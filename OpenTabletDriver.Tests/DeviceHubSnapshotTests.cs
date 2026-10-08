using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NSubstitute;
using OpenTabletDriver.Devices;
using OpenTabletDriver.Plugin.Components;
using OpenTabletDriver.Plugin.Devices;
using Xunit;

namespace OpenTabletDriver.Tests
{
    public class DeviceHubSnapshotTests
    {
        [Fact]
        public void EventData_RemainsStableWhenSourceListsChange()
        {
            var removed = Endpoint("removed");
            var added = Endpoint("added");
            var previous = new List<IDeviceEndpoint> { removed };
            var current = new List<IDeviceEndpoint> { added };
            var changes = new DevicesChangedEventArgs(previous, current);

            previous.Clear();
            current.Clear();

            Assert.Equal(new[] { added }, changes.Additions);
            Assert.Equal(new[] { removed }, changes.Removals);
        }

        [Fact]
        public void GetDevices_ReturnsSnapshotAcrossDeviceNotifications()
        {
            var first = Endpoint("first");
            var child = new FakeHub(first);
            var root = CreateRoot(child);
            var snapshot = root.GetDevices();

            child.SetDevices(Endpoint("second"));

            Assert.Equal(new[] { first }, snapshot);
        }

        [Fact]
        public void HubConnection_ReportsAdditionsAndRemovalsInCorrectDirection()
        {
            var root = CreateRoot(new FakeHub());
            var device = Endpoint("device");
            var child = new FakeHub(device);
            var events = new List<DevicesChangedEventArgs>();
            root.DevicesChanged += (_, changes) => events.Add(changes);

            root.ConnectDeviceHub(child);
            root.DisconnectDeviceHub<FakeHub>();

            Assert.Equal(new[] { device }, events[0].Additions);
            Assert.Empty(events[0].Removals);
            Assert.Contains(events, e => e.Removals.Contains(device));
            Assert.Equal(new[] { device }, events[0].Current);
        }

        [Fact]
        public async Task Notification_CanBeEnumeratedWhileAnotherNotificationUpdatesDevices()
        {
            var child = new FakeHub();
            var root = CreateRoot(child);
            var first = Endpoint("first");
            var second = Endpoint("second");
            var replacement = Endpoint("replacement");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handled = false;
            root.DevicesChanged += (_, changes) =>
            {
                if (handled)
                    return;
                handled = true;
                try
                {
                    using var additions = changes.Additions.GetEnumerator();
                    Assert.True(additions.MoveNext());
                    // Device notifications update the hub synchronously before their debounce delay.
                    child.SetDevices(replacement);
                    Assert.True(additions.MoveNext());
                    Assert.False(additions.MoveNext());
                    Assert.Equal(new[] { first, second }, changes.Current);
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            };

            child.SetDevices(first, second);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task ReentrantNotification_StartsFromLastPublishedDevices()
        {
            var child = new FakeHub();
            var root = CreateRoot(child);
            var first = Endpoint("first");
            var second = Endpoint("second");
            var completion = new TaskCompletionSource<DevicesChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handled = false;
            root.DevicesChanged += (_, changes) =>
            {
                if (!handled)
                {
                    handled = true;
                    child.SetDevices(second);
                }
                else
                {
                    completion.TrySetResult(changes);
                }
            };

            child.SetDevices(first);
            var next = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { first }, next.Previous);
            Assert.Equal(new[] { second }, next.Additions);
            Assert.Equal(new[] { first }, next.Removals);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task HubChange_IncludesPendingDeviceChanges(bool connect)
        {
            var child = new FakeHub();
            var root = CreateRoot(child);
            var device = Endpoint("device");
            var other = new FakeHub();
            if (!connect)
                root.ConnectDeviceHub(other);
            var completion = new TaskCompletionSource<DevicesChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            root.DevicesChanged += (_, changes) =>
            {
                if (changes.Additions.Contains(device))
                    completion.TrySetResult(changes);
            };

            child.SetDevices(device);
            if (connect)
                root.ConnectDeviceHub(other);
            else
                root.DisconnectDeviceHub(other);

            var published = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(published.Removals);
        }

        private static RootHub CreateRoot(IDeviceHub child)
        {
            var provider = Substitute.For<IDeviceHubsProvider>();
            provider.DeviceHubs.Returns(new[] { child });
            return new RootHub(provider);
        }

        private static IDeviceEndpoint Endpoint(string path)
        {
            var endpoint = Substitute.For<IDeviceEndpoint>();
            endpoint.DevicePath.Returns(path);
            return endpoint;
        }

        private sealed class FakeHub(params IDeviceEndpoint[] initialDevices) : IDeviceHub
        {
            private IDeviceEndpoint[] devices = initialDevices;

            public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;

            public IEnumerable<IDeviceEndpoint> GetDevices() => devices;

            public void SetDevices(params IDeviceEndpoint[] updatedDevices)
            {
                var previous = devices;
                devices = updatedDevices;
                DevicesChanged?.Invoke(this, new DevicesChangedEventArgs(previous, devices));
            }
        }
    }
}
