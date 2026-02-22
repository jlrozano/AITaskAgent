namespace BRMS.StdRules.Modules.Database;

public interface IDataBaseQuery
{
    Task<bool> IsValid(string dbName, string query);
    Task<T> Value<T>(string dbName, string query);
}
