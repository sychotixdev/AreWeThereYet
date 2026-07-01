using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using SharpDX;

namespace AreWeThereYet;

public class TaskNode
{
    /// <summary>
    /// The position of the task in world space.
    /// </summary>
    public Vector3 WorldPosition { get; set; }
    /// <summary>
    /// The position of the task in UI space.
    /// </summary>
    public Vector2 UiPosition { get; set; }
    /// <summary>
    /// Type of task we are performing. Different tasks have different underlying logic
    /// </summary>
    public TaskNodeType Type { get; set; }
    /// <summary>
    /// Bounds represents how close we must get to the node to complete it. 
    /// Some tasks require multiple actions within Bounds to be marked as complete.
    /// </summary>
    public int Bounds { get; set; }

    /// <summary>
    /// Counts the number of times the Task has been executed. Used for canceling invalid actions
    /// </summary>
    public int AttemptCount { get; set; }
    public LabelOnGround LabelOnGround { get; set; }
    /// <summary>
    /// The AreaTransition entity backing a Transition task. Used instead of a ground
    /// label so we can rely on the entity's own position and Targetable component
    /// rather than a (sometimes unreliable) UI label.
    /// </summary>
    public Entity TransitionEntity { get; set; }

    /// <summary>
    /// When we first started hovering a transition entity waiting for it to report
    /// isTargeted == true. Null while we haven't started waiting (or after it's been
    /// reset). Used to time out the "hover and wait" loop so we don't spin forever if
    /// the entity never becomes targetable (e.g. a blocking popup, or a bad hover point).
    /// </summary>
    public System.DateTime? TargetWaitStartTime { get; set; }

    public TaskNode(Vector3 position, int bounds, TaskNodeType type = TaskNodeType.Movement)
    {
        WorldPosition = position;
        Type = type;
        Bounds = bounds;
    }

    public TaskNode(LabelOnGround labelOnGround, int bounds, TaskNodeType type = TaskNodeType.Movement)
    {
        LabelOnGround = labelOnGround;
        WorldPosition = labelOnGround?.ItemOnGround?.Pos ?? Vector3.Zero;
        Type = type;
        Bounds = bounds;
    }

    public TaskNode(Entity transitionEntity, int bounds, TaskNodeType type = TaskNodeType.Transition)
    {
        TransitionEntity = transitionEntity;
        WorldPosition = transitionEntity?.Pos ?? Vector3.Zero;
        Type = type;
        Bounds = bounds;
    }
}

public enum TaskNodeType
{
    Movement,
    Transition,
    Loot,
    MercenaryOptIn
}
