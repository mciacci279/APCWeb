using System.Data;
using Microsoft.Data.SqlClient;

namespace APC.Core.Datos;

public static class Db
{
    public static async Task<IDbConnection> AbrirAsync(string connectionString)
    {
        var cn = new SqlConnection(connectionString);
        await cn.OpenAsync();
        return cn;
    }
}
