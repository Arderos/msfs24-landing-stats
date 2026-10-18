using LandingStats.Runtime.Contracts;
namespace LandingStats.Workers;
public static class EntryPoint
{
    public static IWorkerRole Create() => new LandingStats.App.Runtime.ControllersWorkerRole();
}
