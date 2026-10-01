using StackExchange.Redis;
namespace InternalCommunicationApp.Data;

public class RedisConnection
{
    private readonly ConnectionMultiplexer _connection;
    public RedisConnection(IConfiguration configuration)
    {
        var connectionString = 
            configuration.GetConnectionString("Redis")??
            throw new InvalidOperationException("Redis connection string is missing!");
        _connection = ConnectionMultiplexer.Connect(connectionString);
    }
    public IDatabase Database => _connection.GetDatabase();
}