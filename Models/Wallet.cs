using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace momo_wallet.Models;

public class Wallet
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(15)]
    public string PhoneNumber { get; set; } // This acts as the MoMo account number

    [Required]
    [MaxLength(100)]
    public string AccountName { get; set; }

    // decimal is mandatory for financial applications to prevent rounding errors
    [Column(TypeName = "decimal(18,2)")]
    public decimal Balance { get; set; } 
    
    [Required]
    public string Network { get; set; } // MTN, Telecel, AT

    [Required]
    [MaxLength(100)]
    public string Pin { get; set; }  // Defaulting to "0000" so your existing test wallets don't crash the database!


    [Timestamp]
    public uint Version { get; set; }
}
