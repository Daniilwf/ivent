namespace GameEvent.Engine.Kernel;

/// <summary>Generates identifiers for new entities. Generated ids are written into events.</summary>
public interface IIdGenerator
{
    Guid NewId();
}
