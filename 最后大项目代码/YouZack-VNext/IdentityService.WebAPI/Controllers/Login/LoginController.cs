﻿using CaptchaGen.NetCore;
using IdentityService.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Security.Claims;
using Zack.Commons;
using Zack.JWT;

namespace IdentityService.WebAPI.Controllers.Login;

[Route("[controller]/[action]")]
[ApiController]
public class LoginController : ControllerBase
{
    private readonly IIdDomainService idService;
    private readonly ILogger<LoginController> logger;
    private readonly IConnectionMultiplexer redisConnMultiplexer;
    private readonly IOptions<JWTOptions> optJWT;
    //private readonly ITokenService tokenService;

    public LoginController(IIdDomainService idService,
        IConnectionMultiplexer redisConnMultiplexer,
        IOptions<JWTOptions> optJWT)//, ITokenService tokenService)
    {
        this.idService = idService;
        this.redisConnMultiplexer = redisConnMultiplexer;
        this.optJWT = optJWT;
        //this.tokenService = tokenService;
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<ActionResult> CreateWorld()
    {
        if (await idService.FindByNameAsync("admin") != null)
        {
            return StatusCode((int)HttpStatusCode.Conflict, "已经初始化过了");
        }
        User user = new User("admin");
        var r = await idService.CreateAsync(user, "123456");
        Debug.Assert(r.Succeeded);
        var token = await idService.GenerateChangePhoneNumberTokenAsync(user, "18918999999");
        var cr = await idService.ChangePhoneNumAsync(user.Id, "18918999999", token);
        Debug.Assert(cr.Succeeded);
        r = await idService.AddToRoleAsync(user, "User");
        Debug.Assert(r.Succeeded);
        r = await idService.AddToRoleAsync(user, "Admin");
        Debug.Assert(r.Succeeded);
        return Ok();
    }

    [HttpGet]
    [Authorize]
    public async Task<ActionResult<UserResponse>> GetUserInfo()
    {
        string userId = this.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await idService.FindByIdAsync(Guid.Parse(userId));
        if (user == null)//可能用户注销了
        {
            return NotFound();
        }
        //出于安全考虑，不要机密信息传递到客户端
        return new UserResponse(user.Id, user.PhoneNumber, user.CreationTime);
    }

    /// <summary>
    /// 创建一个生成验证码图片用的TicketId
    /// </summary>
    /// <returns></returns>
    [HttpPost]
    [AllowAnonymous]
    public Guid CreateTicketIdForCaptcha()
    {
        return Guid.NewGuid();
    }

    /// <summary>
    /// 创建验证码图片
    /// </summary>
    /// <param name="ticketId">验证码图片的ticketId，客户端提交验证码的时候也带着它</param>
    /// <returns></returns>
    [HttpGet]
    [AllowAnonymous]

    public async Task<ActionResult> CreateCaptchaImage([FromQuery] Guid ticketId)
    {
        var db = redisConnMultiplexer.GetDatabase();
        string captchaCode = ImageFactory.CreateCode(5);
        //把ticketId和正确验证的对应关系保存到redis
        //有效期60秒
        await db.StringSetAsync($"CaptchaImage.{ticketId}", captchaCode,
            expiry: TimeSpan.FromSeconds(60));
        using (var picStream = ImageFactory.BuildImage(captchaCode, 50, 100, 20, 8,
            imgFormat: ImageFormat.Jpeg))
        {
            byte[] bytes = await picStream.ToArrayAsync();
            return File(bytes, "image/png");
        }
    }

    private async Task<bool> ValidateCaptchaAsync(Guid ticketId, string inputCaptcha)
    {
        var db = redisConnMultiplexer.GetDatabase();
        string redisKey = $"CaptchaImage.{ticketId}";
        string captcha = await db.StringGetAsync(redisKey);
        if (captcha == null)
        {
            return false;
        }
        //无论校验成功还是失败，则删掉redis对应关系，客户端需要重新调用CreateCaptchaImage生成新的图片
        //避免爆破或者通过验证的验证码被重复使用
        await db.KeyDeleteAsync(redisKey);
        return captcha.EqualsIgnoreCase(inputCaptcha);
    }

    //书中的项目只提供根据用户名登录的功能，以及管理员增删改查，像用户主动注册、手机验证码登录等功能都不弄。

    [AllowAnonymous]
    [HttpPost]
    public async Task<ActionResult<string>> LoginByPhoneAndPwd(LoginByPhoneAndPwdRequest req)
    {
        if (!await ValidateCaptchaAsync(req.TicketId, req.Captcha))
        {
            return BadRequest("验证码错误");
        }
        (var checkResult, string? token) = await idService.LoginByPhoneAndPwdAsync(req.PhoneNum, req.Password);
        if(checkResult.Succeeded)
        {
            return token!;
        }
        else if (checkResult.IsLockedOut)
        {
            //尝试登录次数太多
            return StatusCode((int)HttpStatusCode.Locked, "此账号已经锁定");
        }
        else
        {
            string msg = checkResult.ToString();
            return StatusCode((int)HttpStatusCode.BadRequest, msg);
        }
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<ActionResult<string>> LoginByUserNameAndPwd(LoginByUserNameAndPwdRequest req)
    {
        if (!await ValidateCaptchaAsync(req.TicketId, req.Captcha))
        {
            return BadRequest("验证码错误");
        }
        (var checkResult,var token) = await idService.LoginByUserNameAndPwdAsync(req.UserName, req.Password);
        if (checkResult.Succeeded)
        {
            return token!;
        }
        else if (checkResult.IsLockedOut)
        {
            //尝试登录次数太多
            return StatusCode((int)HttpStatusCode.Locked, "用户已经被锁定");
        }
        else
        {
            string msg = checkResult.ToString();
            return BadRequest("登录失败" + msg);
        }
    }

    [HttpPost]
    [Authorize]
    public async Task<ActionResult> ChangeMyPassword(ChangeMyPasswordRequest req)
    {
        if (!await ValidateCaptchaAsync(req.TicketId, req.Captcha))
        {
            return BadRequest("验证码错误");
        }
        Guid userId = Guid.Parse(this.User.FindFirstValue(ClaimTypes.NameIdentifier));
        var resetPwdResult = await idService.ChangePasswordAsync(userId, req.Password);
        if (resetPwdResult.Succeeded)
        {
            return Ok();
        }
        else
        {
            return BadRequest(resetPwdResult.Errors.SumErrors());
        }
    }
}