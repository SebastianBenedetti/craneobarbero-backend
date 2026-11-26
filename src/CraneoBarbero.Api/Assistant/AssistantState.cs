using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Assistant;

public sealed class AssistantConversationStore(IMemoryCache cache, IOptions<AssistantSettings> options)
{
    public AssistantConversation? Find(string conversationId, int userId) =>
        cache.TryGetValue(Key(conversationId), out AssistantConversation? conversation) && conversation!.UserId == userId
            ? conversation
            : null;

    public AssistantConversation Create(int userId)
    {
        var conversation = new AssistantConversation { Id = Guid.NewGuid().ToString("N"), UserId = userId };
        Save(conversation);
        return conversation;
    }

    public void Save(AssistantConversation conversation) =>
        cache.Set(Key(conversation.Id), conversation, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(options.Value.ConversationMinutes),
            Size = 1,
        });

    private static string Key(string conversationId) => "assistant:conversation:" + conversationId;
}

public sealed class AssistantQuota(BusinessClock clock, IOptions<AssistantSettings> options)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, (DateTimeOffset Window, int Count)> _messages = [];
    private readonly Dictionary<int, (DateOnly Day, int Count)> _aiByUser = [];
    private (DateTimeOffset Window, int Count) _aiGlobal;

    public void EnsureMessageAllowed(int userId)
    {
        var window = MinuteOf(clock.UtcNow);
        lock (_lock)
        {
            if (_messages.Count > 10_000)
            {
                foreach (var stale in _messages.Where(entry => entry.Value.Window < window).Select(entry => entry.Key).ToList())
                {
                    _messages.Remove(stale);
                }
            }

            var current = _messages.TryGetValue(userId, out var entry) && entry.Window == window ? entry.Count : 0;
            if (current >= options.Value.MessagesPerMinutePerUser)
            {
                throw new TooManyRequestsException("Mandaste muchos mensajes seguidos. Esperá un minuto y seguimos.");
            }

            _messages[userId] = (window, current + 1);
        }
    }

    public bool TryUseAi(int userId)
    {
        var settings = options.Value;
        var window = MinuteOf(clock.UtcNow);
        var today = clock.Today;
        lock (_lock)
        {
            var global = _aiGlobal.Window == window ? _aiGlobal.Count : 0;
            var user = _aiByUser.TryGetValue(userId, out var entry) && entry.Day == today ? entry.Count : 0;
            if (global >= settings.AiMessagesPerMinute || user >= settings.AiMessagesPerUserPerDay)
            {
                return false;
            }

            if (_aiByUser.Count > 10_000)
            {
                foreach (var stale in _aiByUser.Where(item => item.Value.Day != today).Select(item => item.Key).ToList())
                {
                    _aiByUser.Remove(stale);
                }
            }

            _aiGlobal = (window, global + 1);
            _aiByUser[userId] = (today, user + 1);
            return true;
        }
    }

    private static DateTimeOffset MinuteOf(DateTimeOffset instant) =>
        new(instant.Year, instant.Month, instant.Day, instant.Hour, instant.Minute, 0, TimeSpan.Zero);
}
