namespace CMF.Traps
{
    public interface ITrap
    {
        string TrapId { get; }
        float Duration { get; }

        bool LocksMovement { get; }

        bool LocksJump { get; }

        void OnApply();

        void OnRemove();
    }
}