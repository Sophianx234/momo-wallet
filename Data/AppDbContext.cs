using Microsoft.EntityFrameworkCore;
using momo_wallet.Models; 

namespace momo_wallet.Data;

// Inheriting from DbContext gives this class all the Entity Framework database superpowers
public class AppDbContext : DbContext
{
    // This constructor takes the connection string from Program.cs and connects to Neon
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // This tells EF Core: "Look at my C# 'Wallet' and 'Transaction' models and create Postgres tables named 'Wallets' and 'Transactions'"
    public DbSet<Wallet> Wallets { get; set; }
    public DbSet<Transaction> Transactions { get; set; }
}