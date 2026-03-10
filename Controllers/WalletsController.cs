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
if (!BCrypt.Net.BCrypt.Verify(request.Pin, wallet.Pin))
        {
            return Unauthorized(new { message = "Incorrect MoMo PIN." }); 
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
        // ---> 1. IDEMPOTENCY CHECK: REQUIRE THE HEADER <---
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKeyValues))
        {
            return BadRequest(new { message = "Idempotency-Key header is required for financial transactions." });
        }
        
        string idempotencyKey = idempotencyKeyValues.ToString();

        // ---> 2. IDEMPOTENCY CHECK: HAVE WE SEEN THIS KEY BEFORE? <---
        var existingTransaction = await _context.Transactions
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey);

        if (existingTransaction != null)
        {
            // If we found it, DO NOT process the transfer again! 
            // Just return a success message pretending we just did it.
            return Ok(new 
            { 
                message = "Transfer already processed (Idempotent response).", 
                amountSent = existingTransaction.Amount
            });
        }

        // 3. SECURITY CHECK: No zero or negative transfers
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
            IdempotencyKey = idempotencyKey,
            TransactionType = "Transfer"
        };

        _context.Transactions.Add(transferRecord);

        // 8. THE ATOMIC SAVE
        await _context.SaveChangesAsync();

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // If PostgreSQL blocks a double-spend, it throws a DbUpdateConcurrencyException.
            // We catch it here and return a polite error instead of crashing the server!
            return Conflict(new { 
                message = "Transaction could not be completed due to simultaneous account activity. Please check your balance and try again." 
            });
        }

        // 9. Return success
        return Ok(new 
        { 
            message = "Transfer successful", 
            amountSent = request.Amount,
            senderNewBalance = senderWallet.Balance 
        });
    }

        // 9. Return success
        
// The route now accepts queries like: /api/wallets/0241234567/history?page=1&pageSize=5
    // We use POST so we can safely hide the PIN inside the request body
    // The route is now: POST /api/wallets/history?page=1&pageSize=10
    [HttpPost("history")]
    public async Task<IActionResult> GetTransactionHistory(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 10, 
        [FromBody] HistoryRequestDto request = null) // We grab the JSON body here
    {
        if (request == null || string.IsNullOrEmpty(request.PhoneNumber) || string.IsNullOrEmpty(request.Pin))
        {
            return BadRequest(new { message = "Phone number and PIN are required." });
        }

        // 1. FETCH THE WALLET
        var wallet = await _context.Wallets
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.PhoneNumber);
        
        if (wallet == null) return NotFound(new { message = "Wallet not found." });

        // ---> 2. SECURITY CHECK: VERIFY THE BCRYPT PIN <---
        if (!BCrypt.Net.BCrypt.Verify(request.Pin, wallet.Pin))
        {
            return Unauthorized(new { message = "Incorrect MoMo PIN. Access denied." }); 
        }

        // 3. THE SECURE QUERY
        var query = _context.Transactions
            .Where(t => t.SenderPhoneNumber == request.PhoneNumber || t.ReceiverPhoneNumber == request.PhoneNumber)
            .OrderByDescending(t => t.CreatedAt);

        // 4. PAGINATION LOGIC
        var totalRecords = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalRecords / (double)pageSize);

        var history = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new 
        { 
            phoneNumber = request.PhoneNumber,
            currentPage = page,
            totalPages = totalPages,
            pageSize = pageSize,
            totalRecords = totalRecords,
            transactions = history 
        });
    }
    // We changed this to HttpPost so it can securely accept the PIN in a JSON body!
    [HttpPost("balance")]
    public async Task<IActionResult> GetBalance([FromBody] BalanceRequestDto request)
    {
        if (request == null || string.IsNullOrEmpty(request.PhoneNumber) || string.IsNullOrEmpty(request.Pin))
        {
            return BadRequest(new { message = "Phone number and PIN are required." });
        }

        // 1. THE OPTIMIZED QUERY
        var walletInfo = await _context.Wallets
            .Where(w => w.PhoneNumber == request.PhoneNumber)
            .Select(w => new 
            { 
                w.AccountName, 
                w.Balance,
                w.Pin // Grab the hashed PIN from the database
            })
            .FirstOrDefaultAsync();

        // 2. EXISTENCE CHECK
        if (walletInfo == null)
        {
            return NotFound(new { message = "Wallet not found." });
        }

        // ---> 3. SECURITY CHECK: VERIFY THE BCRYPT PIN <---
        // Notice we are comparing against 'walletInfo.Pin' now!
        if (!BCrypt.Net.BCrypt.Verify(request.Pin, walletInfo.Pin))
        {
            return Unauthorized(new { message = "Incorrect MoMo PIN." }); 
        }

        // 4. Return the exact data needed for the mobile app screen
        return Ok(new 
        { 
            phoneNumber = request.PhoneNumber,
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
try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { 
                message = "Withdrawal could not be completed due to simultaneous account activity. Please try again." 
            });
        }

        return Ok(new 
        { 
            message = "Withdrawal successful", 
            amountWithdrawn = request.Amount,
            newBalance = wallet.Balance 
        });
    }
    
    [HttpPost("reverse")]
    public async Task<IActionResult> ReverseTransfer([FromBody] ReversalDto request)
    {
        // 1. FETCH THE ORIGINAL TRANSACTION
        var originalTx = await _context.Transactions.FindAsync(request.TransactionId);

        if (originalTx == null) 
            return NotFound(new { message = "Transaction not found." });

        // 2. CONSTRAINT: Only Transfers can be reversed
        if (originalTx.TransactionType != "Transfer") 
            return BadRequest(new { message = "Only transfers can be reversed." });

        // 3. CONSTRAINT: 24-Hour Time Limit
        // Assuming your Transaction model has a CreatedAt property that sets the date automatically
        if (originalTx.CreatedAt < DateTime.UtcNow.AddHours(-24))
        {
            return BadRequest(new { message = "Transfers older than 24 hours cannot be reversed." });
        }

        // 4. CONSTRAINT: Prevent Double Reversals
        // Since we don't have an "IsReversed" column, we check if a Reversal transaction already exists 
        // for this exact amount between these two people after the original transaction date.
        var alreadyReversed = await _context.Transactions.AnyAsync(t =>
            t.TransactionType == "Reversal" &&
            t.Amount == originalTx.Amount &&
            t.SenderPhoneNumber == originalTx.ReceiverPhoneNumber && // Flipped!
            t.ReceiverPhoneNumber == originalTx.SenderPhoneNumber && // Flipped!
            t.CreatedAt > originalTx.CreatedAt);

        if (alreadyReversed)
            return BadRequest(new { message = "This transaction has already been reversed." });

        // 5. FETCH BOTH WALLETS
        var originalSenderWallet = await _context.Wallets
            .FirstOrDefaultAsync(w => w.PhoneNumber == originalTx.SenderPhoneNumber);
            
        var originalReceiverWallet = await _context.Wallets
            .FirstOrDefaultAsync(w => w.PhoneNumber == originalTx.ReceiverPhoneNumber);

        if (originalSenderWallet == null || originalReceiverWallet == null)
            return BadRequest(new { message = "One of the associated wallets no longer exists." });

        // 6. CONSTRAINT: AUTHORIZATION
        if (!BCrypt.Net.BCrypt.Verify(request.SenderPin, originalSenderWallet.Pin))
        {
            return Unauthorized(new { message = "Incorrect MoMo PIN." }); 
        }

        // 7. CONSTRAINT: FUNDS AVAILABILITY
        // If the receiver already cashed out the money, we cannot reverse it.
        if (originalReceiverWallet.Balance < originalTx.Amount)
        {
            return BadRequest(new { message = "Reversal failed. The receiver has already moved or withdrawn the funds." });
        }

        // 8. THE COMPENSATING MONEY MOVEMENT (Backwards)
        originalReceiverWallet.Balance -= originalTx.Amount;
        originalSenderWallet.Balance += originalTx.Amount;

        // 9. CREATE THE COMPENSATING RECEIPT
        var reversalRecord = new Transaction
        {
            // Notice how Sender and Receiver are flipped!
            SenderPhoneNumber = originalTx.ReceiverPhoneNumber, 
            ReceiverPhoneNumber = originalTx.SenderPhoneNumber, 
            Amount = originalTx.Amount,
            TransactionType = "Reversal"
        };

        _context.Transactions.Add(reversalRecord);

        // 10. THE ATOMIC SAVE
 try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { 
                message = "Reversal could not be completed due to simultaneous account activity. Please try again." 
            });
        }

        return Ok(new 
        { 
            message = "Transfer successfully reversed.", 
            amountReturned = originalTx.Amount,
            newBalance = originalSenderWallet.Balance 
        });
    }

public class DepositDto
{
    public string PhoneNumber { get; set; }
    public decimal Amount { get; set; }
    public string Pin { get; set; } // Withdrawals require a PIN!
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



public class ReversalDto
{
    public int TransactionId { get; set; } // The exact ID of the transfer to reverse
    public string SenderPin { get; set; }  // The sender must authorize the reversal
    public string Pin { get; set; } // <--- ADD THIS LINE
}

public class HistoryRequestDto
{
    public string PhoneNumber { get; set; }
    public string Pin { get; set; }
}

public class BalanceRequestDto
{
    public string PhoneNumber { get; set; }
    public string Pin { get; set; }
}
}