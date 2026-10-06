using Microsoft.Extensions.Time.Testing;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class BackChannelLogoutReplayCacheTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private const string Issuer = "https://idp.example.com";

    private static BackChannelLogoutReplayCache CreateCache(TimeProvider timeProvider)
        => new(timeProvider);

    // ── Reservation ─────────────────────────────────────────────────────────

    [Test]
    public void TryReserve_FirstClaimOfToken_Succeeds()
    {
        var cache = CreateCache(new FakeTimeProvider(_epoch));

        var reservation = cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));

        reservation.Should().Be(BackChannelLogoutReservation.Reserved);
        cache.Count.Should().Be(1);
    }

    [Test]
    public void TryReserve_WhileClaimIsInFlight_RefusesTheDuplicate()
    {
        var cache = CreateCache(new FakeTimeProvider(_epoch));
        cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));

        var reservation = cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));

        reservation.Should().Be(BackChannelLogoutReservation.Duplicate);
        cache.Count.Should().Be(1);
    }

    [Test]
    public void TryReserve_SameJtiFromAnotherIssuer_Succeeds()
    {
        var cache = CreateCache(new FakeTimeProvider(_epoch));
        cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));

        var reservation = cache.TryReserve("https://other-idp.example.com", "jti-1", _epoch.AddMinutes(5));

        reservation.Should().Be(BackChannelLogoutReservation.Reserved);
        cache.Count.Should().Be(2);
    }

    [Test]
    public void TryReserve_ConcurrentClaimsOfSameToken_SingleWinner()
    {
        var cache = CreateCache(new FakeTimeProvider(_epoch));
        var expiresAt = _epoch.AddMinutes(5);
        var wins = 0;

        Parallel.For(0, 64, _ =>
        {
            if (cache.TryReserve(Issuer, "jti-shared", expiresAt) == BackChannelLogoutReservation.Reserved)
            {
                Interlocked.Increment(ref wins);
            }
        });

        wins.Should().Be(1);
        cache.Count.Should().Be(1);
    }

    // ── Completion and release ──────────────────────────────────────────────

    [Test]
    public void Complete_KeepsTheClaimUntilTheTokenExpires()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var cache = CreateCache(timeProvider);
        cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));
        cache.Complete(Issuer, "jti-1");

        var whileValid = cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));
        timeProvider.Advance(TimeSpan.FromMinutes(6));
        var afterExpiry = cache.TryReserve(Issuer, "jti-1", timeProvider.GetUtcNow().AddMinutes(5));

        whileValid.Should().Be(BackChannelLogoutReservation.Duplicate);
        afterExpiry.Should().Be(BackChannelLogoutReservation.Reserved);
        cache.Count.Should().Be(1);
    }

    [Test]
    public void Release_AfterAFailedClaim_LetsTheRetryReserve()
    {
        var cache = CreateCache(new FakeTimeProvider(_epoch));
        cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));

        cache.Release(Issuer, "jti-1");

        cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5))
            .Should().Be(BackChannelLogoutReservation.Reserved);
        cache.Count.Should().Be(1);
    }

    [Test]
    public void Release_AfterCompletion_KeepsTheClaim()
    {
        var cache = CreateCache(new FakeTimeProvider(_epoch));
        cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));
        cache.Complete(Issuer, "jti-1");

        cache.Release(Issuer, "jti-1");

        cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5))
            .Should().Be(BackChannelLogoutReservation.Duplicate);
    }

    [Test]
    public void CompleteAndRelease_OfAnUnknownClaim_DoNothing()
    {
        var cache = CreateCache(new FakeTimeProvider(_epoch));

        cache.Complete(Issuer, "jti-never-seen");
        cache.Release(Issuer, "jti-never-seen");

        cache.Count.Should().Be(0);
    }

    [Test]
    public void TryReserve_StaleInFlightClaim_IsReclaimedAfterItsExpiry()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var cache = CreateCache(timeProvider);
        cache.TryReserve(Issuer, "jti-1", _epoch.AddMinutes(5));

        timeProvider.Advance(TimeSpan.FromMinutes(6));

        cache.TryReserve(Issuer, "jti-1", timeProvider.GetUtcNow().AddMinutes(5))
            .Should().Be(BackChannelLogoutReservation.Reserved);
        cache.Count.Should().Be(1);
    }

    // ── Capacity ────────────────────────────────────────────────────────────

    [Test]
    public void TryReserve_BeyondCapacity_FailsClosed()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var cache = CreateCache(timeProvider);
        FillToCapacity(cache, _epoch.AddHours(1));

        var overflow = cache.TryReserve(Issuer, "jti-overflow", _epoch.AddHours(2));

        overflow.Should().Be(BackChannelLogoutReservation.Full);
        cache.Count.Should().Be(BackChannelLogoutReplayCache.MaxEntries);
    }

    [Test]
    public void TryReserve_AtCapacity_DoesNotEvictAnUnexpiredClaim()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var cache = CreateCache(timeProvider);

        // Every held claim expires before the token that would displace it, so
        // an evicting cache would throw one of them out to make room.
        FillToCapacity(cache, _epoch.AddSeconds(90));
        var displacing = cache.TryReserve(Issuer, "jti-later-expiry", _epoch.AddMinutes(5));

        displacing.Should().Be(BackChannelLogoutReservation.Full);
        cache.Count.Should().Be(BackChannelLogoutReplayCache.MaxEntries);
        cache.TryReserve(Issuer, "jti-0", _epoch.AddSeconds(90))
            .Should().Be(BackChannelLogoutReservation.Duplicate);
    }

    [Test]
    public void TryReserve_AfterHeldClaimsExpire_ReclaimsOnlyExpiredSpace()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        var cache = CreateCache(timeProvider);

        FillToCapacity(cache, _epoch.AddMinutes(5), BackChannelLogoutReplayCache.MaxEntries - 1);
        cache.TryReserve(Issuer, "jti-survivor", _epoch.AddHours(1))
            .Should().Be(BackChannelLogoutReservation.Reserved);

        timeProvider.Advance(TimeSpan.FromMinutes(6));

        var afterExpiry = cache.TryReserve(Issuer, "jti-new", _epoch.AddHours(2));
        var survivor = cache.TryReserve(Issuer, "jti-survivor", _epoch.AddHours(1));

        afterExpiry.Should().Be(BackChannelLogoutReservation.Reserved);
        survivor.Should().Be(BackChannelLogoutReservation.Duplicate);
        cache.Count.Should().Be(2);
    }

    private static void FillToCapacity(
        BackChannelLogoutReplayCache cache,
        DateTimeOffset expiresAt,
        int count = BackChannelLogoutReplayCache.MaxEntries)
    {
        for (var i = 0; i < count; i++)
        {
            var reservation = cache.TryReserve(Issuer, $"jti-{i}", expiresAt);
            reservation.Should().Be(BackChannelLogoutReservation.Reserved);
        }
    }
}
