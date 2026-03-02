using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace momo_wallet.Models;

public class Transaction
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string SenderPhoneNumber { get; set; }

    [Required]
    public string ReceiverPhoneNumber { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Required]
    public string TransactionType { get; set; } // "Transfer", "Deposit", "Withdrawal"
}