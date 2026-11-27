using MapChooser.Contract;
using MapChooser.Models;
using MapChooser.Dependencies;
using MapChooser.Helpers;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Players;

using MapChooser.Menu;
namespace MapChooser.Commands;

public class NominateCommand
{
    private readonly ISwiftlyCore _core;
    private readonly PluginState _state;
    private readonly MapLister _mapLister;
    private readonly MapCooldown _mapCooldown;
    private readonly MapChooserConfig _config;
    private readonly MapChooserMenus _menus;


    public NominateCommand(ISwiftlyCore core, PluginState state, MapLister mapLister, MapCooldown mapCooldown, MapChooserConfig config, MapChooserMenus menus)
    {
        _core = core;
        _state = state;
        _mapLister = mapLister;
        _mapCooldown = mapCooldown;
        _config = config;
        _menus = menus;
    }

    public void Execute(ICommandContext context)
    {
        if (!context.IsSentByPlayer || context.Sender is not { IsValid: true } player || !CanNominate(player)) return;

        string query = string.Join(" ", context.Args).Trim();
        if (query.Length == 0)
        {
            _menus.Open(player, BuildPage(player));
            return;
        }

        // An exact display name (or map/workshop ID) always wins, even if currently unavailable.
        // Do not silently nominate a different map when the requested one is on cooldown.
        var exact = FindByName(query) ?? _mapLister.Maps.FirstOrDefault(map =>
            string.Equals(map.Id, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            TryNominate(player, exact);
            return;
        }

        var matches = _mapLister.Maps
            .Where(map => map.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(map => map.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(map => map.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            ReplyNotFound(player, query);
            return;
        }

        GetCurrentMap(out var mapId, out var workshopId);
        var humans = MapAvailability.CountHumans(_core);
        var available = matches.Where(map =>
            MapAvailability.Of(map, mapId, workshopId, _mapCooldown, humans) == MapChooserAvailability.Available).ToList();

        if (available.Count == 1)
            TryNominate(player, available[0]);
        else if (available.Count > 1)
            _menus.Open(player, BuildPage(player, available));
        else if (matches.Count == 1)
            TryNominate(player, matches[0]); // Give the specific localized reason.
        else
            ReplyNotFound(player, query);
    }

    /// <summary>The nominate page, behind the checks <c>!nominate</c> runs first; null, with the reason in chat, when one fails.</summary>
    public IMenu? BuildMenu(IPlayer player) => CanNominate(player) ? BuildPage(player) : null;

    private IMenu BuildPage(IPlayer player, IReadOnlyList<Map>? matches = null) =>
        new NominateMenu(_core, _mapLister, _mapCooldown).Build(player, HandleNomination, matches);

    private Map? FindByName(string mapName) => _mapLister.Maps.FirstOrDefault(map =>
        string.Equals(map.Name, mapName, StringComparison.OrdinalIgnoreCase));

    private void GetCurrentMap(out string mapId, out string workshopId)
    {
        _core.TryGetEngineSnapshot(out mapId, out workshopId, out _);
        if (string.IsNullOrEmpty(mapId))
            mapId = _core.ConVar.FindAsString("mapname")?.ValueAsString ?? string.Empty;
    }

    private void ReplyNotFound(IPlayer player, string name)
    {
        var local = _core.Translation.GetPlayerLocalizer(player);
        player.SendChat(local["map_chooser.prefix"] + " " + local["map_chooser.nominate.not_found", name]);
    }

    private bool CanNominate(IPlayer player)
    {
        if (!_config.Rtv.NominationEnabled) return false;

        if (_state.WarmupRunning)
        {
            var localizer = _core.Translation.GetPlayerLocalizer(player);
            player.SendChat(localizer["map_chooser.prefix"] + " " + localizer["map_chooser.general.validation.warmup"]);
            return false;
        }

        if (!_config.AllowSpectatorsToVote && player.Controller?.TeamNum == 1)
        {
            var localizer = _core.Translation.GetPlayerLocalizer(player);
            player.SendChat(localizer["map_chooser.prefix"] + " " + localizer["map_chooser.general.validation.spectator"]);
            return false;
        }

        return true;
    }

    private void HandleNomination(IPlayer player, string mapName)
    {
        if (!player.IsValid || !CanNominate(player)) return;
        // A menu row names a specific map. A stale row never falls back to a fuzzy match.
        var map = FindByName(mapName);
        if (map is null) ReplyNotFound(player, mapName);
        else TryNominate(player, map);
    }

    private void TryNominate(IPlayer player, Map map)
    {
        var localizer = _core.Translation.GetPlayerLocalizer(player);
        GetCurrentMap(out var mapId, out var workshopId);
        var available = MapAvailability.Of(map, mapId, workshopId, _mapCooldown, MapAvailability.CountHumans(_core));
        string? reason = available switch
        {
            MapChooserAvailability.CurrentMap => localizer["map_chooser.nominate.current_map"],
            MapChooserAvailability.Cooldown => localizer["map_chooser.votemap.cooldown", map.Name],
            MapChooserAvailability.TooFewPlayers or MapChooserAvailability.TooManyPlayers
                => localizer["map_chooser.general.validation.player_count", map.Name],
            _ => null
        };
        if (available != MapChooserAvailability.Available)
        {
            player.SendChat(localizer["map_chooser.prefix"] + " " + (reason ?? localizer["map_chooser.nominate.not_found", map.Name]));
            return;
        }

        _state.Nominate(player.Slot, map.Name);
        _core.PlayerManager.SendChat(localizer["map_chooser.prefix"] + " " + _core.Localizer["map_chooser.nominate.success", player.Controller?.PlayerName ?? "Unknown", map.Name]);
    }

    public List<string> GetNominations()
    {
        return _state.Nominations.Values.Distinct().ToList();
    }

    public void Clear()
    {
        _state.ClearNominations();
    }
}
