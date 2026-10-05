using InternalCommunicationApp.Data;
using InternalCommunicationApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

public class LoginModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<User> _passwordHasher;

    public LoginModel(AppDbContext db)
    {
        _db = db;
        _passwordHasher = new PasswordHasher<User>();
    }

    [BindProperty]
    [Required]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var user = await EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(
                _db.Users,
                u => u.Username == Username);

        if (user == null)
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var result = _passwordHasher.VerifyHashedPassword(
            user,
            user.PasswordHash,
            Password);

        if (result == PasswordVerificationResult.Failed)
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
        };
        var identity = new ClaimsIdentity(claims
        , CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal);
        return RedirectToPage("/Index");
    }
}