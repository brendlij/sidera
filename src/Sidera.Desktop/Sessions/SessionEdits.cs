using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Rigs;

namespace Sidera.Desktop.Sessions;

/// <summary>Where a list of actions is: the start or the end of the session, the preparation of a target, or a block.</summary>
public enum ActionPlace
{
    Start,
    End,
    Preparation,
    Block,
}

/// <summary>A list of actions of a session. <see cref="Owner"/> is the target (for the preparation) or the block it belongs to; <c>null</c> for the start and the end.</summary>
public readonly record struct ActionOwner(ActionPlace Place, Guid? Owner = null)
{
    public static ActionOwner Start { get; } = new(ActionPlace.Start);

    public static ActionOwner End { get; } = new(ActionPlace.End);

    public static ActionOwner PreparationOf(Guid target) => new(ActionPlace.Preparation, target);

    public static ActionOwner BlockOf(Guid block) => new(ActionPlace.Block, block);

    /// <summary>Where the actions of this place may go in the library.</summary>
    public ActionScope Scope => Place switch
    {
        ActionPlace.Block => ActionScope.Block,
        ActionPlace.Preparation => ActionScope.Preparation,
        _ => ActionScope.Session,
    };
}

/// <summary>
/// What a user does to a session, as functions from a session to a session: nothing is changed in place, an element that is not found changes nothing, and every element is found by its id (they are unique
/// in a session). The editor calls these and shows the result; the tests call them without any screen.
/// </summary>
public static class SessionEdits
{
    // ---- finding

    public static SessionTarget? FindTarget(SessionDefinition session, Guid id) => session.Targets.FirstOrDefault(t => t.Id == id);

    public static SetupLane? FindLane(SessionDefinition session, Guid id) => session.Targets.SelectMany(t => t.Lanes).FirstOrDefault(l => l.Id == id);

    public static SequenceBlock? FindBlock(SessionDefinition session, Guid id) => session.Targets.SelectMany(t => t.Lanes).SelectMany(l => l.Blocks).FirstOrDefault(b => b.Id == id);

    public static SessionAction? FindAction(SessionDefinition session, Guid id) => AllActions(session).FirstOrDefault(a => a.Id == id);

    /// <summary>The target that holds a lane, block or action (or is the target itself); <c>null</c> for an action of the start or the end.</summary>
    public static SessionTarget? TargetOf(SessionDefinition session, Guid id) => session.Targets.FirstOrDefault(t =>
        t.Id == id || t.Lanes.Any(l => l.Id == id || l.Blocks.Any(b => b.Id == id || b.Actions.Any(a => a.Id == id))) || t.Preparation.Any(a => a.Id == id));

    public static SetupLane? LaneOf(SessionDefinition session, Guid blockOrAction) =>
        session.Targets.SelectMany(t => t.Lanes).FirstOrDefault(l => l.Blocks.Any(b => b.Id == blockOrAction || b.Actions.Any(a => a.Id == blockOrAction)));

    public static IEnumerable<SessionAction> AllActions(SessionDefinition session) =>
        session.Start.Concat(session.End).Concat(session.Targets.SelectMany(t => t.Preparation.Concat(t.Lanes.SelectMany(l => l.Blocks.SelectMany(b => b.Actions)))));

    public static ActionOwner? OwnerOf(SessionDefinition session, Guid action)
    {
        if (session.Start.Any(a => a.Id == action))
        {
            return ActionOwner.Start;
        }

        if (session.End.Any(a => a.Id == action))
        {
            return ActionOwner.End;
        }

        foreach (var target in session.Targets)
        {
            if (target.Preparation.Any(a => a.Id == action))
            {
                return ActionOwner.PreparationOf(target.Id);
            }

            foreach (var block in target.Lanes.SelectMany(l => l.Blocks))
            {
                if (block.Actions.Any(a => a.Id == action))
                {
                    return ActionOwner.BlockOf(block.Id);
                }
            }
        }

        return null;
    }

    public static IReadOnlyList<SessionAction> ActionsOf(SessionDefinition session, ActionOwner owner) => owner.Place switch
    {
        ActionPlace.Start => session.Start,
        ActionPlace.End => session.End,
        ActionPlace.Preparation => session.Targets.FirstOrDefault(t => t.Id == owner.Owner)?.Preparation ?? [],
        _ => FindBlock(session, owner.Owner ?? Guid.Empty)?.Actions ?? [],
    };

    // ---- targets

    public static SessionDefinition ReplaceTarget(SessionDefinition session, Guid id, Func<SessionTarget, SessionTarget> change) =>
        session with { Targets = session.Targets.Select(t => t.Id == id ? change(t) : t).ToList() };

    public static SessionDefinition AddTarget(SessionDefinition session, SessionTarget target, int? index = null)
    {
        var targets = session.Targets.ToList();
        targets.Insert(Math.Clamp(index ?? targets.Count, 0, targets.Count), target);
        return session with { Targets = targets };
    }

    public static SessionDefinition RemoveTarget(SessionDefinition session, Guid id) => session with { Targets = session.Targets.Where(t => t.Id != id).ToList() };

    public static SessionDefinition MoveTarget(SessionDefinition session, Guid id, int delta) => session with { Targets = Moved(session.Targets, t => t.Id == id, delta) };

    /// <summary>A copy of a target right after it, with new ids everywhere.</summary>
    public static SessionDefinition DuplicateTarget(SessionDefinition session, Guid id, Guid newId)
    {
        var index = session.Targets.ToList().FindIndex(t => t.Id == id);
        return index < 0 ? session : AddTarget(session, WithFreshIds(session.Targets[index], newId) with { Name = session.Targets[index].Name + " (copy)" }, index + 1);
    }

    // ---- lanes

    public static SessionDefinition ReplaceLane(SessionDefinition session, Guid id, Func<SetupLane, SetupLane> change) =>
        session with { Targets = session.Targets.Select(t => t with { Lanes = t.Lanes.Select(l => l.Id == id ? change(l) : l).ToList() }).ToList() };

    public static SessionDefinition AddLane(SessionDefinition session, Guid target, SetupLane lane) =>
        ReplaceTarget(session, target, t => t with { Lanes = [.. t.Lanes, lane] });

    public static SessionDefinition RemoveLane(SessionDefinition session, Guid id) =>
        session with { Targets = session.Targets.Select(t => t with { Lanes = t.Lanes.Where(l => l.Id != id).ToList() }).ToList() };

    // ---- blocks

    public static SessionDefinition ReplaceBlock(SessionDefinition session, Guid id, Func<SequenceBlock, SequenceBlock> change) =>
        ReplaceLanes(session, l => l with { Blocks = l.Blocks.Select(b => b.Id == id ? change(b) : b).ToList() });

    public static SessionDefinition AddBlock(SessionDefinition session, Guid lane, SequenceBlock block, int? index = null) =>
        ReplaceLane(session, lane, l =>
        {
            var blocks = l.Blocks.ToList();
            blocks.Insert(Math.Clamp(index ?? blocks.Count, 0, blocks.Count), block);
            return l with { Blocks = blocks };
        });

    public static SessionDefinition RemoveBlock(SessionDefinition session, Guid id) => ReplaceLanes(session, l => l with { Blocks = l.Blocks.Where(b => b.Id != id).ToList() });

    public static SessionDefinition MoveBlock(SessionDefinition session, Guid id, int delta) => ReplaceLanes(session, l => l with { Blocks = Moved(l.Blocks, b => b.Id == id, delta) });

    /// <summary>A copy of a block right after it, with new ids; its automation and limits are kept.</summary>
    public static SessionDefinition DuplicateBlock(SessionDefinition session, Guid id, Guid newId) =>
        ReplaceLanes(session, l =>
        {
            var index = l.Blocks.ToList().FindIndex(b => b.Id == id);
            if (index < 0)
            {
                return l;
            }

            var blocks = l.Blocks.ToList();
            blocks.Insert(index + 1, WithFreshIds(blocks[index], newId));
            return l with { Blocks = blocks };
        });

    // ---- actions

    public static SessionDefinition AddAction(SessionDefinition session, ActionOwner owner, SessionAction action, int? index = null)
    {
        List<SessionAction> Inserted(IReadOnlyList<SessionAction> list)
        {
            var actions = list.ToList();
            actions.Insert(Math.Clamp(index ?? actions.Count, 0, actions.Count), action);
            return actions;
        }

        return ChangeActions(session, owner, Inserted);
    }

    public static SessionDefinition RemoveAction(SessionDefinition session, Guid id) => ChangeEveryList(session, list => list.Where(a => a.Id != id).ToList());

    public static SessionDefinition MoveAction(SessionDefinition session, Guid id, int delta) => ChangeEveryList(session, list => Moved(list, a => a.Id == id, delta));

    public static SessionDefinition ReplaceAction(SessionDefinition session, Guid id, Func<SessionAction, SessionAction> change) =>
        ChangeEveryList(session, list => list.Select(a => a.Id == id ? change(a) : a).ToList());

    // ---- copies

    /// <summary>A block with new ids for itself and its actions.</summary>
    public static SequenceBlock WithFreshIds(SequenceBlock block, Guid id) => block with { Id = id, Actions = block.Actions.Select(a => a with { Id = Guid.NewGuid() }).ToList() };

    public static SessionTarget WithFreshIds(SessionTarget target, Guid id) => target with
    {
        Id = id,
        Preparation = target.Preparation.Select(a => a with { Id = Guid.NewGuid() }).ToList(),
        Lanes = target.Lanes.Select(l => new SetupLane(Guid.NewGuid(), l.Setup, l.Blocks.Select(b => WithFreshIds(b, Guid.NewGuid())).ToList())).ToList(),
    };

    // ---- mechanics

    private static SessionDefinition ReplaceLanes(SessionDefinition session, Func<SetupLane, SetupLane> change) =>
        session with { Targets = session.Targets.Select(t => t with { Lanes = t.Lanes.Select(change).ToList() }).ToList() };

    private static SessionDefinition ChangeActions(SessionDefinition session, ActionOwner owner, Func<IReadOnlyList<SessionAction>, List<SessionAction>> change) => owner.Place switch
    {
        ActionPlace.Start => session with { Start = change(session.Start) },
        ActionPlace.End => session with { End = change(session.End) },
        ActionPlace.Preparation => ReplaceTarget(session, owner.Owner ?? Guid.Empty, t => t with { Preparation = change(t.Preparation) }),
        _ => ReplaceBlock(session, owner.Owner ?? Guid.Empty, b => b with { Actions = change(b.Actions) }),
    };

    // An edit of a list that is applied to every list there is: only the one that holds the action changes (ids are unique).
    private static SessionDefinition ChangeEveryList(SessionDefinition session, Func<IReadOnlyList<SessionAction>, List<SessionAction>> change) => session with
    {
        Start = change(session.Start),
        End = change(session.End),
        Targets = session.Targets.Select(t => t with
        {
            Preparation = change(t.Preparation),
            Lanes = t.Lanes.Select(l => l with { Blocks = l.Blocks.Select(b => b with { Actions = change(b.Actions) }).ToList() }).ToList(),
        }).ToList(),
    };

    private static List<T> Moved<T>(IReadOnlyList<T> list, Func<T, bool> which, int delta)
    {
        var items = list.ToList();
        var index = items.FindIndex(new Predicate<T>(which));
        var to = index + delta;
        if (index < 0 || to < 0 || to >= items.Count)
        {
            return items;
        }

        var item = items[index];
        items.RemoveAt(index);
        items.Insert(to, item);
        return items;
    }

    /// <summary>The imaging paths that the lanes of a target use, in order; lanes that name none (the only setup) are not in it.</summary>
    public static IReadOnlyList<ImagingBindingId> BoundSetups(SessionTarget target) => target.Lanes.Select(l => l.Setup).OfType<ImagingBindingId>().ToList();
}
