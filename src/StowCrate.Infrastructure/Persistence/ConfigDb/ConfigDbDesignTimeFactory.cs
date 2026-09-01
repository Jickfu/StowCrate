using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed class ConfigDbDesignTimeFactory : IDesignTimeDbContextFactory<ConfigDbContext>
{
    public ConfigDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite("Data Source=config.design.db").Options;
        return new ConfigDbContext(options);
    }
}
