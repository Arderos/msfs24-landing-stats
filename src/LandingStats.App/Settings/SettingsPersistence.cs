using System;
using System.Threading.Tasks;

namespace LandingStats.App.Settings;

internal static class SettingsPersistence
{
    public static Task SaveAsync(
        ApplicationSettingsRepository repository,
        ApplicationSettings snapshot,
        Action? apply = null,
        Action? rollback = null) => Task.Run(() =>
    {
        apply?.Invoke();
        try
        {
            repository.Save(snapshot);
        }
        catch (Exception saveException)
        {
            try
            {
                rollback?.Invoke();
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    saveException.Message + " " + rollbackException.Message,
                    saveException, rollbackException);
            }
            throw;
        }
    });
}
