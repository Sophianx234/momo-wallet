using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore; // Required for .FirstOrDefaultAsync()
using momo_wallet.Data;
using momo_wallet.Models;
using System.Threading.Tasks;

namespace momo_wallet.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WalletsController : ControllerBase
{
    private readonly AppDbContext _context;

    public WalletsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost]
    public async Task<IActionResult> CreateWallet([FromBody] Wallet newWallet)
    {
        // 1. SECURITY CHECK: Does this phone number already exist in Neon?
        var existingWallet = await _context.Wallets
            .FirstOrDefaultAsync(w => w.PhoneNumber == newWallet.PhoneNumber);

        if (existingWallet != null)
        {
            // Return a 400 Bad Request if the number is taken
            return BadRequest(new { message = "A MoMo wallet with this phone number already exists." });
        }

        // 2. SECURITY CHECK: Force the starting balance to 0.00
        // This prevents malicious users from sending { "balance": 50000 } in their JSON request!
        newWallet.Balance = 0;
        // SECURITY CHECK: Scramble the MoMo PIN before saving to the database
        if(string.IsNullOrEmpty(newWallet.Pin) || newWallet.Pin.Length != 4)
        {
            return BadRequest(new { message = "PIN must be exactly 4 characters long." });
        }
        // This turns "1234" into a secure hash like "$2a$11$abcdefg..."
        newWallet.Pin = BCrypt.Net.BCrypt.HashPassword(newWallet.Pin);
        
        // 3. Save the new wallet to the PostgreSQL database
        _context.Wallets.Add(newWallet);
        await _context.SaveChangesAsync();


        // 4. Return a 201 Created status with the new wallet data
        return Created("", newWallet);
    }

    [HttpPost("deposit")]
    public async Task<IActionResult> Deposit([FromBody] DepositDto request)
    {
        // 1. SECURITY CHECK: Never allow negative or zero deposits!
        if (request.Amount <= 0)
        {
            return BadRequest(new { message = "Deposit amount must be greater than zero." });
        }

        // 2. Find the exact wallet we are depositing into
        var wallet = await _context.Wallets
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.PhoneNumber);

        if (wallet == null)
        {
            return NotFound(new { message = "Wallet not found." });
        }

        // 3. Add the money to the balance
        wallet.Balance += request.Amount;

        // 4. Create the digital receipt (The Transaction record)
        var newTransaction = new Transaction
        {
            SenderPhoneNumber = "SYSTEM", // Since it's a deposit, the sender is the system/agent
            ReceiverPhoneNumber = wallet.PhoneNumber,
            Amount = request.Amount,
            TransactionType = "Deposit"
        };

        // Tell EF Core to track this new transaction
        _context.Transactions.Add(newTransaction);

        // 5. THE MAGIC LINE: This updates the Wallet AND saves the Transaction at the exact same time
        await _context.SaveChangesAsync();

        // 6. Return a success message with the new balance
        return Ok(new 
        { 
            message = "Deposit successful", 
            newBalance = wallet.Balance 
        });
    }

   [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] TransferDto request)
    {
        // 1. SECURITY CHECK: No zero or negative transfers
        if (request.Amount <= 0)
        {
            return BadRequest(new { message = "Transfer amount must be greater than zero." });
        }

        // 2. SECURITY CHECK: You cannot send money to yourself
        if (request.SenderPhoneNumber == request.ReceiverPhoneNumber)
        {
            return BadRequest(new { message = "Cannot transfer money to the same account." });
        }

        // 3. FETCH BOTH WALLETS
        var senderWallet = await _context.Wallets
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.SenderPhoneNumber);
            
        var receiverWallet = await _context.Wallets
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.ReceiverPhoneNumber);

        // 4. VALIDATE EXISTENCE
        if (senderWallet == null) return NotFound(new { message = "Sender wallet not found." });
        if (receiverWallet == null) return NotFound(new { message = "Receiver wallet not found." });

        // ---> 4.5 NEW SECURITY CHECK: VERIFY THE BCRYPT PIN <---
        // This compares the plain-text PIN from the JSON request against the scrambled hash in Postgres
        if (!BCrypt.Net.BCrypt.Verify(request.Pin, senderWallet.Pin))
        {
            return Unauthorized(new { message = "Incorrect MoMo PIN." }); 
        }

        // 5. VALIDATE FUNDS: Does the sender actually have enough money?
        if (senderWallet.Balance < request.Amount)
        {
            return BadRequest(new { message = "Insufficient funds for this transfer." });
        }

        // 6. THE MONEY MOVEMENT
        senderWallet.Balance -= request.Amount;
        receiverWallet.Balance += request.Amount;

        // 7. CREATE THE DIGITAL RECEIPT
        var transferRecord = new Transaction
        {
            SenderPhoneNumber = senderWallet.PhoneNumber,
            ReceiverPhoneNumber = receiverWallet.PhoneNumber,
            Amount = request.Amount,
            TransactionType = "Transfer"
        };

        _context.Transactions.Add(transferRecord);

        // 8. THE ATOMIC SAVE
        await _context.SaveChangesAsync();

        // 9. Return success
        return Ok(new 
        { 
            message = "Transfer successful", 
            amountSent = request.Amount,
            senderNewBalance = senderWallet.Balance 
        });
    }
// The route now accepts queries like: /api/wallets/0241234567/history?page=1&pageSize=5
    [HttpGet("{phoneNumber}/history")]
    public async Task<IActionResult> GetTransactionHistory(string phoneNumber, [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        var walletExists = await _context.Wallets.AnyAsync(w => w.PhoneNumber == phoneNumber);
        
        if (!walletExists) return NotFound(new { message = "Wallet not found." });

        // 1. Build the base query without executing it yet
        var query = _context.Transactions
            .Where(t => t.SenderPhoneNumber == phoneNumber || t.ReceiverPhoneNumber == phoneNumber)
            .OrderByDescending(t => t.CreatedAt);

        // 2. Count the total records so the frontend knows how many pages exist
        var totalRecords = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalRecords / (double)pageSize);

        // 3. The Pagination Magic: Skip the previous pages, and Take only the current page's amount
        var history = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new 
        { 
            phoneNumber = phoneNumber,
            currentPage = page,
            totalPages = totalPages,
            pageSize = pageSize,
            totalRecords = totalRecords,
            transactions = history 
        });
    }
    [HttpGet("{phoneNumber}/balance")]
    public async Task<IActionResult> GetBalance(string phoneNumber)
    {
        // 1. THE OPTIMIZED QUERY
        // By using .Select(), EF Core translates this to:
        // SELECT "AccountName", "Balance" FROM "Wallets" WHERE "PhoneNumber" = '...'
        // It completely ignores the Id, Network, and other columns!
        var walletInfo = await _context.Wallets
            .Where(w => w.PhoneNumber == phoneNumber)
            .Select(w => new 
            { 
                w.AccountName, 
                w.Balance 
            })
            .FirstOrDefaultAsync();

        // 2. SECURITY CHECK
        if (walletInfo == null)
        {
            return NotFound(new { message = "Wallet not found." });
        }

        // 3. Return the exact data needed for the mobile app screen
        return Ok(new 
        { 
            phoneNumber = phoneNumber,
            accountName = walletInfo.AccountName,
            balance = walletInfo.Balance 
        });
    }

    [HttpGet("resolve/{phoneNumber}")]
    public async Task<IActionResult> ResolveAccountName(string phoneNumber)
    {
        // 1. We ONLY select the AccountName to save bandwidth and protect other data
        var accountName = await _context.Wallets
            .Where(w => w.PhoneNumber == phoneNumber)
            .Select(w => w.AccountName)
            .FirstOrDefaultAsync();

        if (accountName == null)
        {
            return NotFound(new { message = "Wallet not found." });
        }

        // 2. Return just the name so the frontend can display "Transfer to Sophian?"
        return Ok(new 
        { 
            phoneNumber = phoneNumber, 
            accountName = accountName 
        });
    }

    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw([FromBody] WithdrawDto request)
    {
        if (request.Amount <= 0) 
            return BadRequest(new { message = "Withdrawal amount must be greater than zero." });

        var wallet = await _context.Wallets
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.PhoneNumber);

        if (wallet == null) 
            return NotFound(new { message = "Wallet not found." });

        // ---> SECURITY CHECK: VERIFY THE BCRYPT PIN <---
        if (!BCrypt.Net.BCrypt.Verify(request.Pin, wallet.Pin))
        {
            return Unauthorized(new { message = "Incorrect MoMo PIN." }); 
        }

        // VALIDATE FUNDS
        if (wallet.Balance < request.Amount)
        {
            return BadRequest(new { message = "Insufficient funds for this withdrawal." });
        }

        // THE MONEY MOVEMENT
        wallet.Balance -= request.Amount;

        // CREATE THE DIGITAL RECEIPT
        var withdrawalRecord = new Transaction
        {
            SenderPhoneNumber = wallet.PhoneNumber,
            ReceiverPhoneNumber = "AGENT", // The money leaves the system via an agent
            Amount = request.Amount,
            TransactionType = "Withdrawal"
        };

        _context.Transactions.Add(withdrawalRecord);

        // THE ATOMIC SAVE
        await _context.SaveChangesAsync();

        return Ok(new 
        { 
            message = "Withdrawal successful", 
            amountWithdrawn = request.Amount,
            newBalance = wallet.Balance 
        });
    }
}

public class DepositDto
{
    public string PhoneNumber { get; set; }
    public decimal Amount { get; set; }
}


public class TransferDto
{
    public string SenderPhoneNumber { get; set; }
    public string ReceiverPhoneNumber { get; set; }
    public decimal Amount { get; set; }
    public string Pin { get; set; } // <--- ADD THIS LINE
}


public class WithdrawDto
{
    public string PhoneNumber { get; set; }
    public decimal Amount { get; set; }
    public string Pin { get; set; } // Withdrawals require a PIN!
}