using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Testcontainers.PostgreSql;

namespace EFIssue;

[TestFixture]
public class EFIssueTests
{
    private PostgreSqlContainer PostgreSqlContainer { get; set; }
    
    [SetUp]
    public async Task Setup()
    {
        PostgreSqlContainer = new PostgreSqlBuilder()
            .WithImage("postgres:15.6")
            .Build();
        await PostgreSqlContainer.StartAsync();
    }
    
    [TearDown]
    public async Task TearDown()
    {
        await PostgreSqlContainer.DisposeAsync();
    }
    
    [Test]
    public async Task Test()
    {
        var connectionString = PostgreSqlContainer.GetConnectionString();
        
        var options = new DbContextOptionsBuilder<TradeContext>()
            .LogTo(Console.WriteLine, LogLevel.Debug)
            .EnableSensitiveDataLogging()
            .UseNpgsql(connectionString)
            .Options;

        await using var context = new TradeContext(options);
        
        await context.Database.EnsureCreatedAsync();
        
        var trade = new Trade
        {
            Commission = new Commission
            {
                Amount = 1,
                Currency = "USD"
            },
            Quantity = 2,
            Price = 3
        };
        context.Add(trade);
        await context.SaveChangesAsync();
        
        // clear context
        context.ChangeTracker.Clear();
        
        var tradeFromDb = await context.Set<Trade>().FirstAsync(x => x.Id == trade.Id);
        
        // assert values
        trade.Commission.Amount.Should().Be(tradeFromDb.Commission.Amount);
        trade.Commission.Currency.Should().Be(tradeFromDb.Commission.Currency);
        trade.Quantity.Should().Be(tradeFromDb.Quantity);
        trade.Price.Should().Be(tradeFromDb.Price);
        
        // update trade
        /// this results in following sql statement:
        /// Executed DbCommand (2ms) [Parameters=[@p3='1', @p0='5', @p1='4', @p2='EUR' (Nullable = false)], CommandType='Text', CommandTimeout='30']
        /// UPDATE tr.trades SET price = @p0, quantity = @p1, commission_currency = @p2 -- missing update to commission_amount
        /// WHERE id = @p3
        /// RETURNING commission_amount; -- why this??
        tradeFromDb.Commission = new Commission { Amount = 0, Currency = "EUR" }; // OPTION 1: comment this line and use the next lines to make it work
       
        /// using update of existing instance works fine and results in following sql statement (which does not include returning commission_amount and updates it):
        /// Executed DbCommand (2ms) [Parameters=[@p4='1', @p0='5', @p1='4', @p2='0', @p3='EUR' (Nullable = false)], CommandType='Text', CommandTimeout='30']
        /// UPDATE tr.trades SET price = @p0, quantity = @p1, commission_amount = @p2, commission_currency = @p3
        /// WHERE id = @p4;
        //tradeFromDb.Commission.Amount = 0;
        //tradeFromDb.Commission.Currency = "EUR";
        tradeFromDb.Quantity = 4;
        tradeFromDb.Price = 5;

        context.Update(tradeFromDb);
        await context.SaveChangesAsync();
        
        context.ChangeTracker.Clear();
        
        tradeFromDb = await context.Set<Trade>().FirstAsync(x => x.Id == trade.Id);
        
        // assert values
        tradeFromDb.Commission.Amount.Should().Be(0);
        tradeFromDb.Commission.Currency.Should().Be("EUR");
        tradeFromDb.Quantity.Should().Be(4);
        tradeFromDb.Price.Should().Be(5);
    }
}

public class TradeContext: DbContext 
{
    public TradeContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Trade>().HasKey(e => e.Id)
            .HasName("pk_tr_trades");

        modelBuilder.Entity<Trade>().ToTable("trades", "tr");

        modelBuilder.Entity<Trade>().Property(e => e.Id)
            .ValueGeneratedOnAdd()
            .HasColumnName("id");
        
        modelBuilder.Entity<Trade>().Property(e => e.Quantity)
            .HasColumnName("quantity")
            .HasColumnType("numeric(33,10)")
            .IsRequired();
        
        modelBuilder.Entity<Trade>().Property(e => e.Price)
            .HasColumnName("price")
            .HasColumnType("numeric(33,10)")
            .IsRequired();
        
        modelBuilder.Entity<Trade>()
            .OwnsOne(t => t.Commission, b => ConfigureCommission("commission", b))
            .Navigation(e => e.Commission)
            .IsRequired();
    }
    
    private void ConfigureCommission<TEntity>(string path, OwnedNavigationBuilder<TEntity, Commission> builder)
        where TEntity : class
    {
        builder.Property(p => p.Amount)
            .HasColumnName($"{path}_amount")
            .HasColumnType("numeric(33,10)")
            .IsRequired()
            .HasDefaultValue(0); // OPTION 2: comment this line and it works fine

        builder.Property(p => p.Currency)
            .HasColumnName($"{path}_currency")
            .IsRequired();
    }
}

public class Trade
{
    public int Id { get; set; }
    public Commission Commission { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

public class Commission
{
    public decimal Amount { get; set; }
    public string Currency { get; set; }
}



