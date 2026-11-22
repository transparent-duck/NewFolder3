namespace MasterPlayer.MechanicRuntime;

// Auto is the game policy. Offline callers can select a solver while retaining
// the same hint model, continuation management, evaluation and route contract.
public enum EncounterPlannerMethod { Auto, Sipp, Forward, Beam, LayerTree, ICem, Shooting }
