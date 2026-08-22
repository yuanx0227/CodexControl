using System.Collections.Concurrent;
using System.Security.Cryptography;
using CodexControl.Protocol;

namespace CodexControl.Relay.Authentication;

public sealed record IssuedChallenge(
    string ChallengeId,
    string Nonce,
    DateTimeOffset ExpiresAt);

public sealed class ChallengeStore
{
    private readonly ConcurrentDictionary<string, ChallengeEntry> _entries = new();

    public IssuedChallenge Create(
        string connectionId,
        PrincipalRole role,
        string principalId)
    {
        var challenge = new IssuedChallenge(
            string.Concat("chl_", Guid.NewGuid().ToString("N")),
            Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
            DateTimeOffset.UtcNow.AddSeconds(15));
        _entries[challenge.ChallengeId] = new ChallengeEntry(
            connectionId,
            role,
            principalId,
            challenge.Nonce,
            challenge.ExpiresAt);
        CleanupExpired();
        return challenge;
    }

    public bool TryConsume(
        string challengeId,
        string connectionId,
        PrincipalRole role,
        string principalId,
        out IssuedChallenge challenge)
    {
        challenge = null!;
        if (!_entries.TryRemove(challengeId, out var entry) ||
            entry.ConnectionId != connectionId ||
            entry.Role != role ||
            entry.PrincipalId != principalId ||
            entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return false;
        }

        challenge = new IssuedChallenge(challengeId, entry.Nonce, entry.ExpiresAt);
        return true;
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _entries)
        {
            if (entry.Value.ExpiresAt < now)
            {
                _entries.TryRemove(entry.Key, out _);
            }
        }
    }

    private sealed record ChallengeEntry(
        string ConnectionId,
        PrincipalRole Role,
        string PrincipalId,
        string Nonce,
        DateTimeOffset ExpiresAt);
}
