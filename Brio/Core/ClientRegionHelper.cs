using Dalamud.Plugin.Services;
using System.Linq;

namespace Brio.Core;

/// <summary>
/// Provides the small set of client-region checks required by the CN build.
/// </summary>
public static class ClientRegionHelper
{
    private static bool? _isChineseClient;

    public static bool IsChineseClient()
    {
        if(_isChineseClient.HasValue)
            return _isChineseClient.Value;

        if(global::Brio.Brio.TryGetService<IPlayerState>(out var playerState)
           && playerState.IsLoaded
           && playerState.HomeWorld.IsValid)
        {
            var homeWorldName = playerState.HomeWorld.Value.Name.ToString();
            _isChineseClient = homeWorldName.Any(c => c is >= '\u4E00' and <= '\u9FFF');
            return _isChineseClient.Value;
        }

        // Do not cache the result until player data is ready. Brio can be loaded
        // before login or while the character is still entering the world.
        return false;
    }
}
