using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Friday.Infrastructure.Data;

public class FridayDbContextFactory : IDesignTimeDbContextFactory<FridayDbContext>
{
    public FridayDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FridayDbContext>();
        optionsBuilder.UseSqlite("Data Source=friday.db");
        return new FridayDbContext(optionsBuilder.Options);
    }
}

