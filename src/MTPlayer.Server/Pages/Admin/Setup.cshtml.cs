using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using MTPlayer.Server.Admin;

namespace MTPlayer.Server.Pages.Admin;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class SetupModel(AdminSetupService setup, IAntiforgery antiforgery) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await setup.IsAvailableAsync(cancellationToken) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var jsonRequest = Request.HasJsonContentType();
        if (jsonRequest)
        {
            Input = await JsonSerializer.DeserializeAsync<SetupInput>(
                    Request.Body,
                    JsonOptions,
                    cancellationToken) ?? new SetupInput();
        }
        else
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
            if (!ModelState.IsValid)
            {
                return Page();
            }
        }

        var result = await setup.CreateAsync(Input.Token, Input.Email, Input.Password, cancellationToken);
        if (jsonRequest)
        {
            return result.Status switch
            {
                AdminSetupStatus.Success => new JsonResult(new { result.Email }),
                AdminSetupStatus.NotFound => NotFound(),
                AdminSetupStatus.InvalidToken => StatusCode(StatusCodes.Status403Forbidden),
                AdminSetupStatus.DuplicateEmail => new ConflictObjectResult(new { message = "该邮箱已存在。" }),
                _ => BadRequest(new { message = "邮箱格式或密码长度无效。" }),
            };
        }

        switch (result.Status)
        {
            case AdminSetupStatus.Success:
                return RedirectToPage("/Admin/Login");
            case AdminSetupStatus.NotFound:
                return NotFound();
            case AdminSetupStatus.InvalidToken:
                ModelState.AddModelError(string.Empty, "初始化令牌无效。");
                break;
            case AdminSetupStatus.DuplicateEmail:
                ModelState.AddModelError(string.Empty, "该邮箱已存在。");
                break;
            default:
                ModelState.AddModelError(string.Empty, "邮箱格式或密码长度无效。");
                break;
        }

        return Page();
    }

    public sealed class SetupInput
    {
        [Required(ErrorMessage = "请输入初始化令牌。")]
        public string? Token { get; set; }

        [Required(ErrorMessage = "请输入管理员邮箱。")]
        [EmailAddress(ErrorMessage = "邮箱格式无效。")]
        public string? Email { get; set; }

        [Required(ErrorMessage = "请输入管理员密码。")]
        [StringLength(128, MinimumLength = 10, ErrorMessage = "密码长度必须为 10 到 128 个字符。")]
        public string? Password { get; set; }
    }
}
