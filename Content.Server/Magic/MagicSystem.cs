using System.Numerics;
using Content.Server.Body.Components;
using Content.Server.Chat.Systems;
using Content.Shared.Actions;
using Content.Shared.Body.Components;
using Content.Shared.Chat;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Magic;
using Content.Shared.Magic.Components;
using Content.Shared.Magic.Events;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Storage;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Spawners;


namespace Content.Server.Magic;

public sealed class MagicSystem : SharedMagicSystem
{
    [Dependency] private readonly ChatSystem _chat = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SpeakSpellEvent>(OnSpellSpoken);
    }

    private void OnSpellSpoken(ref SpeakSpellEvent args)
    {
        if (args.Handled)
            return;

        AttemptLearn(uid, component, args);

        args.Handled = true;
    }

    private void AttemptLearn(EntityUid uid, SpellbookComponent component, UseInHandEvent args)
    {
        var doAfterEventArgs = new DoAfterArgs(EntityManager, args.User, component.LearnTime, new SpellbookDoAfterEvent(), uid, target: uid)
        {
            BreakOnDamage = true,
            BreakOnMove = true,
            NeedHand = true //What, are you going to read with your eyes only??
        };

        _doAfter.TryStartDoAfter(doAfterEventArgs);
    }

    #region Spells

    /// <summary>
    /// Handles the instant action (i.e. on the caster) attempting to spawn an entity.
    /// </summary>
    private void OnInstantSpawn(InstantSpawnSpellEvent args)
    {
        if (args.Handled)
            return;

        var transform = Transform(args.Performer);

        foreach (var position in GetSpawnPositions(transform, args.Pos))
        {
            var ent = Spawn(args.Prototype, position.SnapToGrid(EntityManager, _mapManager));

            if (args.PreventCollideWithCaster)
            {
                var comp = EnsureComp<PreventCollideComponent>(ent);
                comp.Uid = args.Performer;
            }
        }

        Speak(args);
        args.Handled = true;
    }

    private void OnProjectileSpell(ProjectileSpellEvent ev)
    {
        if (ev.Handled)
            return;

        ev.Handled = true;
        Speak(ev);

        var xform = Transform(ev.Performer);
        var userVelocity = _physics.GetMapLinearVelocity(ev.Performer);

        foreach (var pos in GetSpawnPositions(xform, ev.Pos))
        {
            // If applicable, this ensures the projectile is parented to grid on spawn, instead of the map.
            var mapPos = pos.ToMap(EntityManager);
            var spawnCoords = _mapManager.TryFindGridAt(mapPos, out var gridUid, out _)
                ? pos.WithEntityId(gridUid, EntityManager)
                : new(_mapManager.GetMapEntityId(mapPos.MapId), mapPos.Position);

            var ent = Spawn(ev.Prototype, spawnCoords);
            var direction = ev.Target.ToMapPos(EntityManager, _transformSystem) -
                            spawnCoords.ToMapPos(EntityManager, _transformSystem);
            _gunSystem.ShootProjectile(ent, direction, userVelocity, ev.Performer, ev.Performer);
        }
    }

    private void OnChangeComponentsSpell(ChangeComponentsSpellEvent ev)
    {
        if (ev.Handled)
            return;
        ev.Handled = true;
        Speak(ev);

        foreach (var toRemove in ev.ToRemove)
        {
            if (_compFact.TryGetRegistration(toRemove, out var registration))
                RemComp(ev.Target, registration.Type);
        }

        foreach (var (name, data) in ev.ToAdd)
        {
            if (HasComp(ev.Target, data.Component.GetType()))
                continue;

            var component = (Component) _compFact.GetComponent(name);
            component.Owner = ev.Target;
            var temp = (object) component;
            _seriMan.CopyTo(data.Component, ref temp);
            EntityManager.AddComponent(ev.Target, (Component) temp!);
        }
    }

    private List<EntityCoordinates> GetSpawnPositions(TransformComponent casterXform, MagicSpawnData data)
    {
        switch (data)
        {
            case TargetCasterPos:
                return new List<EntityCoordinates>(1) {casterXform.Coordinates};
            case TargetInFront:
            {
                // This is shit but you get the idea.
                var directionPos = casterXform.Coordinates.Offset(casterXform.LocalRotation.ToWorldVec().Normalized());

                if (!_mapManager.TryGetGrid(casterXform.GridUid, out var mapGrid))
                    return new List<EntityCoordinates>();

                if (!directionPos.TryGetTileRef(out var tileReference, EntityManager, _mapManager))
                    return new List<EntityCoordinates>();

                var tileIndex = tileReference.Value.GridIndices;
                var coords = mapGrid.GridTileToLocal(tileIndex);
                EntityCoordinates coordsPlus;
                EntityCoordinates coordsMinus;

                var dir = casterXform.LocalRotation.GetCardinalDir();
                switch (dir)
                {
                    case Direction.North:
                    case Direction.South:
                    {
                        coordsPlus = mapGrid.GridTileToLocal(tileIndex + (1, 0));
                        coordsMinus = mapGrid.GridTileToLocal(tileIndex + (-1, 0));
                        return new List<EntityCoordinates>(3)
                        {
                            coords,
                            coordsPlus,
                            coordsMinus,
                        };
                    }
                    case Direction.East:
                    case Direction.West:
                    {
                        coordsPlus = mapGrid.GridTileToLocal(tileIndex + (0, 1));
                        coordsMinus = mapGrid.GridTileToLocal(tileIndex + (0, -1));
                        return new List<EntityCoordinates>(3)
                        {
                            coords,
                            coordsPlus,
                            coordsMinus,
                        };
                    }
                }

                return new List<EntityCoordinates>();
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>
    /// Teleports the user to the clicked location
    /// </summary>
    /// <param name="args"></param>
    private void OnTeleportSpell(TeleportSpellEvent args)
    {
        if (args.Handled)
            return;

        var transform = Transform(args.Performer);

        if (transform.MapID != args.Target.GetMapId(EntityManager)) return;

        _transformSystem.SetCoordinates(args.Performer, args.Target);
        transform.AttachToGridOrMap();
        _audio.PlayPvs(args.BlinkSound, args.Performer, AudioParams.Default.WithVolume(args.BlinkVolume));
        Speak(args);
        args.Handled = true;
    }

    /// <summary>
    /// Opens all doors within range
    /// </summary>
    /// <param name="args"></param>
    private void OnKnockSpell(KnockSpellEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        Speak(args);

        //Get the position of the player
        var transform = Transform(args.Performer);
        var coords = transform.Coordinates;

        _audio.PlayPvs(args.KnockSound, args.Performer, AudioParams.Default.WithVolume(args.KnockVolume));

        //Look for doors and don't open them if they're already open.
        foreach (var entity in _lookup.GetEntitiesInRange(coords, args.Range))
        {
            if (TryComp<DoorBoltComponent>(entity, out var bolts))
                _doorSystem.SetBoltsDown((entity, bolts), false);

            if (TryComp<DoorComponent>(entity, out var doorComp) && doorComp.State is not DoorState.Open)
                _doorSystem.StartOpening(entity);
        }
    }

    private void OnSmiteSpell(SmiteSpellEvent ev)
    {
        if (ev.Handled)
            return;

        ev.Handled = true;
        Speak(ev);

        var direction = Transform(ev.Target).MapPosition.Position - Transform(ev.Performer).MapPosition.Position;
        var impulseVector = direction * 10000;

        _physics.ApplyLinearImpulse(ev.Target, impulseVector);

        if (!TryComp<BodyComponent>(ev.Target, out var body))
            return;

        var ents = _bodySystem.GibBody(ev.Target, true, body);

        if (!ev.DeleteNonBrainParts)
            return;

        foreach (var part in ents)
        {
            // just leaves a brain and clothes
            if (HasComp<BodyComponent>(part) && !HasComp<BrainComponent>(part))
            {
                QueueDel(part);
            }
        }
    }

    /// <summary>
    /// Spawns entity prototypes from a list within range of click.
    /// </summary>
    /// <remarks>
    /// It will offset mobs after the first mob based on the OffsetVector2 property supplied.
    /// </remarks>
    /// <param name="args"> The Spawn Spell Event args.</param>
    private void OnWorldSpawn(WorldSpawnSpellEvent args)
    {
        if (args.Handled)
            return;

        var targetMapCoords = args.Target;

        SpawnSpellHelper(args.Contents, targetMapCoords, args.Lifetime, args.Offset);
        Speak(args);
        args.Handled = true;
    }

    /// <summary>
    /// Loops through a supplied list of entity prototypes and spawns them
    /// </summary>
    /// <remarks>
    /// If an offset of 0, 0 is supplied then the entities will all spawn on the same tile.
    /// Any other offset will spawn entities starting from the source Map Coordinates and will increment the supplied
    /// offset
    /// </remarks>
    /// <param name="entityEntries"> The list of Entities to spawn in</param>
    /// <param name="entityCoords"> Map Coordinates where the entities will spawn</param>
    /// <param name="lifetime"> Check to see if the entities should self delete</param>
    /// <param name="offsetVector2"> A Vector2 offset that the entities will spawn in</param>
    private void SpawnSpellHelper(List<EntitySpawnEntry> entityEntries, EntityCoordinates entityCoords, float? lifetime, Vector2 offsetVector2)
    {
        var getProtos = EntitySpawnCollection.GetSpawns(entityEntries, _random);

        var offsetCoords = entityCoords;
        foreach (var proto in getProtos)
        {
            // TODO: Share this code with instant because they're both doing similar things for positioning.
            var entity = Spawn(proto, offsetCoords);
            offsetCoords = offsetCoords.Offset(offsetVector2);

            if (lifetime != null)
            {
                var comp = EnsureComp<TimedDespawnComponent>(entity);
                comp.Lifetime = lifetime.Value;
            }
        }
    }

    #endregion

    private void Speak(BaseActionEvent args)
    {
        if (args is not ISpeakSpell speak || string.IsNullOrWhiteSpace(speak.Speech))
            return;

        _chat.TrySendInGameICMessage(args.Performer, Loc.GetString(speak.Speech),
            InGameICChatType.Speak, false);
    }
}
