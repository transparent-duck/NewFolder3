using BossMod;
using DeepDungeon.MechanicPlanning;

namespace MasterPlayer.MechanicRuntime;

internal readonly record struct MechanicTransition(int Hits, double Exposure, double FirstViolation, ShapeDistance Region);

// One encounter's optional path-dependent rule alongside ordinary hazards,
// chasing AOEs and knockbacks. Only observed inputs live in the snapshot.
public abstract record EncounterPathMechanic
{
    internal abstract bool Valid { get; }
    internal abstract DateTime Deadline { get; }
    internal abstract DateTime? ResolutionAt { get; }
    internal abstract double RequiredHorizon(DateTime now);
    internal abstract bool Initialize(ref MechanicBranchState branch, Vector2 position, DateTime now, double start);
    internal abstract MechanicTransition Advance(ref MechanicBranchState branch, Vector2 from, Vector2 to, double start, double end);
    internal abstract bool Resolve(ref MechanicBranchState branch, double at);
    internal abstract bool GrantsImmunity(in MechanicBranchState branch, uint status);
    internal abstract void Sample(ref MechanicBranchState branch, double time, Random random);
    internal abstract double AddDistance(double distance, in MechanicBranchState first, in MechanicBranchState second);
    internal abstract double BranchDeadline(in MechanicBranchState branch);
    internal abstract PlanScore ScoreTerminal(in MechanicBranchState branch, double time, PlanScore score);
}
