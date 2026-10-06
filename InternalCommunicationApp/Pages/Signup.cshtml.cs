using InternalCommunicationApp.Data;
using InternalCommunicationApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

public class SignupModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<User> _passwordHasher;

    public SignupModel(AppDbContext db)
    {
        _db =db;
        _passwordHasher = new PasswordHasher<User>();
    }
    [BindProperty]
    [Required, StringLength(50, MinimumLength = 3)]
    [RegularExpression(@"^[A-Za-z0-9_.-]+$", ErrorMessage = "Use letters, numbers, dot, dash or underscore.")]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [Required, StringLength(100, MinimumLength = 8)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required, DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    [Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Username == Username);
        if(existingUser != null)
        {
            ErrorMessage = "Username already exists.";
            return Page();
        }
        var user = new User
        {
            Username = Username,
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, Password);
        _db.Users.Add(user);
        try 
        { 
            await _db.SaveChangesAsync(); 
        }
        catch (DbUpdateException)
        {
            ErrorMessage = "Username already exists.";
            return Page();
        }
        return RedirectToPage("/Login");
    }
}