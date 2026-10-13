using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace MiniCore.Infrastructure;

//Conexion a bd

public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString) => _connectionString = connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

//marcador de UTC lo que se lee y se escribe

public sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override DateTime Parse(object value) => DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);

    public override void SetValue(IDbDataParameter parameter, DateTime value)
    {
        parameter.DbType = DbType.DateTime2;
        parameter.Value = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
    }
}

public static class DapperSetup
{
    private static int _done;
    
    public static void Configure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
    }
}