using Microsoft.Extensions.AI;

namespace StarWarsData.Services.AI.Agents.CharacterTimelines;

/// <summary>
/// Wrapper to distinguish the character timeline chat client from the default one in DI.
/// </summary>
public sealed class CharacterTimelineChatClient(IChatClient inner) : DelegatingChatClient(inner);
