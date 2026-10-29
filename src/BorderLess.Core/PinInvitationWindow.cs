using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace BorderLess.Core;

public sealed record PinInvitation(Guid Id, string Code, DateTimeOffset Expires)
{
    public override string ToString() => "BorderLess: código temporal oculto";
}
public sealed record PinInvitationStatus(Guid Id, DateTimeOffset Expires, int AttemptsRemaining, bool Active, bool Busy);

// The budget is global to an invitation, not per IP: changing source addresses
// does not provide additional guesses. One in-flight exchange bounds CPU cost.
public sealed class PinInvitationWindow : IDisposable
{
    internal sealed class Lease : IDisposable
    {
        private readonly PinInvitationWindow owner;
        internal readonly PinInvitation Invitation;
        internal readonly CancellationToken Token;
        internal Lease(PinInvitationWindow owner, PinInvitation invitation, CancellationToken token) { this.owner = owner; Invitation = invitation; Token = token; }
        public void Dispose() => owner.Release(this);
    }
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private PinInvitation? current;
    private CancellationTokenSource? cancelled;
    private int remaining;
    private bool used, busy;
    public event Action? Changed;
    public PinInvitationWindow(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;
    public PinInvitation Create()
    {
        PinInvitation value;
        lock (gate)
        {
            cancelled?.Cancel(); cancelled = new(); remaining = 5; busy = used = false;
            value = new(Guid.NewGuid(), RandomNumberGenerator.GetInt32(100_000_000).ToString("D8", CultureInfo.InvariantCulture), clock.GetUtcNow().AddMinutes(2)); current = value;
        }
        Changed?.Invoke(); return value;
    }
    public PinInvitationStatus Status
    {
        get { lock (gate) return new(current?.Id ?? Guid.Empty, current?.Expires ?? DateTimeOffset.MinValue, remaining, current != null && !used && (remaining > 0 || busy) && current.Expires > clock.GetUtcNow(), busy); }
    }
    internal Lease Begin(Guid expected)
    {
        Lease lease;
        lock (gate)
        {
            if (current == null || used || busy || remaining <= 0 || current.Expires <= clock.GetUtcNow() || (expected != Guid.Empty && expected != current.Id)) throw new AuthenticationException("Código no disponible. Genera uno nuevo en el receptor.");
            remaining--; busy = true; lease = new(this, current, cancelled!.Token);
        }
        Changed?.Invoke(); return lease;
    }
    internal void Consume(Lease lease)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, lease.Invitation) || used || current.Expires <= clock.GetUtcNow() || lease.Token.IsCancellationRequested) throw new AuthenticationException("El código venció o fue revocado.");
            used = true;
        }
        Changed?.Invoke();
    }
    private void Release(Lease lease)
    {
        lock (gate) { if (ReferenceEquals(current, lease.Invitation)) busy = false; }
        Changed?.Invoke();
    }
    public bool IsCodeText(string value)
    {
        lock (gate) return current != null && current.Expires > clock.GetUtcNow() && value.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal) == current.Code;
    }
    public void Revoke()
    {
        lock (gate) { cancelled?.Cancel(); current = null; remaining = 0; busy = false; }
        Changed?.Invoke();
    }
    public void Dispose() => Revoke();
}
