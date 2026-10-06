using Microsoft.Extensions.Time.Testing;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class DenialStateStoreTests
{
    private static DenialStateStore CreateStore(
        TimeProvider? timeProvider = null,
        int capacity = 100,
        TimeSpan? ttl = null)
    {
        return new DenialStateStore(timeProvider ?? new FakeTimeProvider(), capacity, ttl);
    }

    // ── Basic store and retrieve ─────────────────────────────────────────────

    [Test]
    public void Store_ReturnsNonEmptyHandle()
    {
        var store = CreateStore();

        var handle = store.Store("John Doe", "admission_denied");

        handle.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void Retrieve_ValidHandle_ReturnsDisplayNameAndCategory()
    {
        var store = CreateStore();
        var handle = store.Store("John Doe", "admission_denied");

        var result = store.Retrieve(handle);

        result.Should().NotBeNull();
        result!.Value.DisplayName.Should().Be("John Doe");
        result.Value.Category.Should().Be("admission_denied");
    }

    // ── One-time retrieval ───────────────────────────────────────────────────

    [Test]
    public void Retrieve_SecondRetrieveOfSameHandle_ReturnsNull()
    {
        var store = CreateStore();
        var handle = store.Store("John Doe", "admission_denied");

        store.Retrieve(handle);
        var second = store.Retrieve(handle);

        second.Should().BeNull();
    }

    // ── Expiry ───────────────────────────────────────────────────────────────

    [Test]
    public void Retrieve_AfterTtl_ReturnsNull()
    {
        var timeProvider = new FakeTimeProvider();
        var store = CreateStore(timeProvider: timeProvider, ttl: TimeSpan.FromMinutes(5));
        var handle = store.Store("John Doe", "admission_denied");

        timeProvider.Advance(TimeSpan.FromMinutes(6));
        var result = store.Retrieve(handle);

        result.Should().BeNull();
    }

    [Test]
    public void Retrieve_BeforeTtl_ReturnsValue()
    {
        var timeProvider = new FakeTimeProvider();
        var store = CreateStore(timeProvider: timeProvider, ttl: TimeSpan.FromMinutes(5));
        var handle = store.Store("John Doe", "admission_denied");

        timeProvider.Advance(TimeSpan.FromMinutes(4));
        var result = store.Retrieve(handle);

        result.Should().NotBeNull();
        result!.Value.DisplayName.Should().Be("John Doe");
    }

    [Test]
    public void Retrieve_ExactlyAtTtl_ReturnsNull()
    {
        var timeProvider = new FakeTimeProvider();
        var store = CreateStore(timeProvider: timeProvider, ttl: TimeSpan.FromMinutes(5));
        var handle = store.Store("John Doe", "admission_denied");

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var result = store.Retrieve(handle);

        result.Should().BeNull();
    }

    // ── Capacity ─────────────────────────────────────────────────────────────

    [Test]
    public void Store_AtCapacity_EvictsOldestByInsertionOrder()
    {
        var store = CreateStore(capacity: 2);

        var handle1 = store.Store("User 1", "admission_denied");
        var handle2 = store.Store("User 2", "admission_denied");
        var handle3 = store.Store("User 3", "admission_denied");

        store.Retrieve(handle1).Should().BeNull("handle1 was evicted");
        store.Retrieve(handle2).Should().NotBeNull("handle2 is still present");
        store.Retrieve(handle3).Should().NotBeNull("handle3 was just added");
    }

    [Test]
    public void Store_BelowCapacity_DoesNotEvict()
    {
        var store = CreateStore(capacity: 5);

        var handles = Enumerable.Range(1, 4)
            .Select(i => store.Store($"User {i}", "admission_denied"))
            .ToList();

        foreach (var handle in handles)
        {
            store.Retrieve(handle).Should().NotBeNull();
        }
    }

    [Test]
    public void Store_ExpiredEntriesEvictedBeforeCapacity()
    {
        var timeProvider = new FakeTimeProvider();
        var store = CreateStore(timeProvider: timeProvider, capacity: 2, ttl: TimeSpan.FromMinutes(1));

        var handle1 = store.Store("User 1", "admission_denied");
        var handle2 = store.Store("User 2", "admission_denied");

        timeProvider.Advance(TimeSpan.FromMinutes(2));

        var handle3 = store.Store("User 3", "admission_denied");

        store.Retrieve(handle1).Should().BeNull("expired");
        store.Retrieve(handle2).Should().BeNull("expired");
        store.Retrieve(handle3).Should().NotBeNull("fresh");
    }

    // ── Base64Url handle ─────────────────────────────────────────────────────

    [Test]
    public void Store_HandleIsBase64Url_NoPadding()
    {
        var store = CreateStore();

        var handle = store.Store("User", "admission_denied");

        handle.Should().NotContain("=");
        handle.Should().NotContain("+");
        handle.Should().NotContain("/");
    }

    [Test]
    public void Store_GeneratesUniqueHandles()
    {
        var store = CreateStore();

        var handles = Enumerable.Range(1, 10)
            .Select(_ => store.Store("User", "admission_denied"))
            .ToList();

        handles.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void Store_HandleDoesNotContainDisplayName()
    {
        var store = CreateStore();

        var handle = store.Store("John Doe", "admission_denied");

        handle.Should().NotContain("John");
        handle.Should().NotContain("Doe");
        handle.Should().NotContain("admission");
    }

    // ── No sensitive payload ─────────────────────────────────────────────────

    [Test]
    public void Retrieve_ReturnsOnlyDisplayNameAndCategory()
    {
        var store = CreateStore();
        var handle = store.Store("John Doe", "admission_denied");

        var result = store.Retrieve(handle);

        result.Should().NotBeNull();
        result!.Value.DisplayName.Should().Be("John Doe");
        result.Value.Category.Should().Be("admission_denied");
    }

    // ── Invalid handle ───────────────────────────────────────────────────────

    [Test]
    public void Retrieve_InvalidHandle_ReturnsNull()
    {
        var store = CreateStore();

        var result = store.Retrieve("nonexistent-handle");

        result.Should().BeNull();
    }

    [Test]
    public void Retrieve_EmptyHandle_ReturnsNull()
    {
        var store = CreateStore();

        var result = store.Retrieve("");

        result.Should().BeNull();
    }

    // ── Multiple categories ──────────────────────────────────────────────────

    [Test]
    public void Store_DifferentCategories_StoresIndependently()
    {
        var store = CreateStore();

        var handle1 = store.Store("User 1", "admission_denied");
        var handle2 = store.Store("User 2", "session_expired");

        store.Retrieve(handle1)!.Value.Category.Should().Be("admission_denied");
        store.Retrieve(handle2)!.Value.Category.Should().Be("session_expired");
    }

    // ── Bounded insertion-order tracking ─────────────────────────────────────

    [Test]
    public void Store_ManyRetrieveCycles_FirstHandleEvictedAsOldest()
    {
        const int capacity = 2;
        var store = CreateStore(capacity: capacity);

        var first = store.Store("First", "admission_denied");
        store.InsertionTrackingCount.Should().Be(1);

        for (var i = 0; i < 50; i++)
        {
            var handle = store.Store($"Cycle {i}", "admission_denied");
            store.InsertionTrackingCount.Should().BeLessThanOrEqualTo(capacity);
            store.Retrieve(handle).Should().NotBeNull();
            store.InsertionTrackingCount.Should().BeLessThanOrEqualTo(capacity);
        }

        store.InsertionTrackingCount.Should().Be(1, "only first remains live");

        var newer = store.Store("Newer", "admission_denied");
        var trigger = store.Store("Trigger", "admission_denied");

        store.InsertionTrackingCount.Should().BeLessThanOrEqualTo(capacity);

        store.Retrieve(first).Should().BeNull("first is oldest, evicted");
        store.Retrieve(newer).Should().NotBeNull();
        store.Retrieve(trigger).Should().NotBeNull();
    }

    [Test]
    public void Store_AfterRepeatedExpiry_EvictsOldestRemainingEntry()
    {
        var timeProvider = new FakeTimeProvider();
        var store = CreateStore(timeProvider: timeProvider, capacity: 3, ttl: TimeSpan.FromMinutes(1));

        var h1 = store.Store("User 1", "admission_denied");
        var h2 = store.Store("User 2", "admission_denied");
        var h3 = store.Store("User 3", "admission_denied");

        timeProvider.Advance(TimeSpan.FromMinutes(2));

        var h4 = store.Store("User 4", "admission_denied");

        store.Retrieve(h1).Should().BeNull("expired");
        store.Retrieve(h2).Should().BeNull("expired");
        store.Retrieve(h3).Should().BeNull("expired");
        store.Retrieve(h4).Should().NotBeNull("h4 is fresh");
    }

    [Test]
    public void Store_ManyExpiryCycles_CapacityEvictionStillWorks()
    {
        var timeProvider = new FakeTimeProvider();
        var store = CreateStore(timeProvider: timeProvider, capacity: 2, ttl: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 10; i++)
        {
            store.Store($"User {i}", "admission_denied");
            timeProvider.Advance(TimeSpan.FromMinutes(2));
        }

        var h1 = store.Store("Fresh 1", "admission_denied");
        var h2 = store.Store("Fresh 2", "admission_denied");
        var h3 = store.Store("Fresh 3", "admission_denied");

        store.Retrieve(h1).Should().BeNull("evicted as oldest");
        store.Retrieve(h2).Should().NotBeNull();
        store.Retrieve(h3).Should().NotBeNull();
    }

    // ── Concurrency ──────────────────────────────────────────────────────────

    [Test]
    public void Store_ConcurrentStores_DoNotCorruptState()
    {
        var store = CreateStore(capacity: 50);

        var handles = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 100, i =>
        {
            var handle = store.Store($"User {i}", "admission_denied");
            handles.Add(handle);
        });

        handles.Should().OnlyHaveUniqueItems();
        handles.Should().HaveCount(100);
    }

    [Test]
    public void Store_ConcurrentStoreAndRetrieve_DoNotThrow()
    {
        var store = CreateStore(capacity: 20);

        var stored = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 50, i =>
        {
            var handle = store.Store($"User {i}", "admission_denied");
            stored.Add(handle);
        });

        var act = () =>
        {
            Parallel.ForEach(stored, handle =>
            {
                store.Retrieve(handle);
            });
        };

        act.Should().NotThrow();
    }
}
