using Adyen.Checkout.Services;
using Adyen.Checkout.Models;
using adyen_dotnet_checkout_example_advanced.Options;
using adyen_dotnet_checkout_example_advanced.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaymentRequest = Adyen.Checkout.Models.PaymentRequest;

namespace adyen_dotnet_checkout_example_advanced.Controllers
{
    [ApiController]
    public class ApiController : ControllerBase
    {
        private readonly ILogger<ApiController> _logger;
        private readonly IUrlService _urlService;
        private readonly IPaymentsService _paymentsService;
        private readonly string _merchantAccount;
        
        public ApiController(IPaymentsService paymentsService, ILogger<ApiController> logger, IUrlService urlService, IOptions<AdyenOptions> options)
        {
            _logger = logger;
            _urlService = urlService;
            _paymentsService = paymentsService;
            _merchantAccount = options.Value.ADYEN_MERCHANT_ACCOUNT;
            _logger.LogInformation("ApiController initialized");
        }

        [HttpPost("api/getPaymentMethods")]
        public async Task<ActionResult<PaymentMethodsResponse>> GetPaymentMethods(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("GetPaymentMethods called");
            var paymentMethodsRequest = new PaymentMethodsRequest()
            {
                MerchantAccount = _merchantAccount,
                Channel = PaymentMethodsRequest.ChannelEnum.Web
            };
            
            try
            {
                var res = await _paymentsService.PaymentMethodsAsync(paymentMethodsRequest, cancellationToken: cancellationToken);
                _logger.LogInformation($"Response for PaymentMethods:\n{res}\n");
                return res.Ok();
            }
            catch (Adyen.HttpClient.HttpClientException e)
            {
                _logger.LogError($"Request for PaymentMethods failed:\n{e.ResponseBody}\n");
                throw;
            }
        }

        [HttpPost("api/initiatePayment")]
        public async Task<ActionResult<PaymentResponse>> InitiatePayment(PaymentRequest request, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("InitiatePayment called");
            if (!ModelState.IsValid)
            {
                var errors = string.Join(" | ", ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage + (e.Exception != null ? " (" + e.Exception.Message + ")" : "")));
                _logger.LogWarning("InitiatePayment: Invalid ModelState: " + errors);
                return BadRequest(ModelState);
            }

            if (request == null)
            {
                _logger.LogWarning("InitiatePayment: request is null");
                return BadRequest("Request is null");
            }
            else
            {
                _logger.LogInformation("InitiatePayment request: " + request.ToString());
            }
            var orderRef = Guid.NewGuid();
            var paymentRequest = new PaymentRequest()
            {
                MerchantAccount = _merchantAccount, // Required.
                Reference = orderRef.ToString(), // Required.
                Channel = PaymentRequest.ChannelEnum.Web,
                Amount = new Amount("EUR", 10000), // Value is 100€ in minor units.
                
                // Required for 3DS2 redirect flow.
                ReturnUrl = $"{_urlService.GetHostUrl()}/api/handleShopperRedirect?orderRef={orderRef}",

                // Used for klarna, klarna is not supported everywhere, hence why we've defaulted to countryCode "NL" as it supports the following payment methods below:
                // "Pay now", "Pay later" and "Pay over time", see docs for more info: https://docs.adyen.com/payment-methods/klarna#supported-countries.
                CountryCode = "NL", 
                LineItems = new List<LineItem>()
                {
                    new LineItem(quantity: 1, amountIncludingTax: 5000, description: "Sunglasses"),
                    new LineItem(quantity: 1, amountIncludingTax: 5000, description: "Headphones")
                },
                
                // We strongly recommend that you the billingAddress in your request. 
                // Card schemes require this for channel web, iOS, and Android implementations.
                //BillingAddress = new BillingAddress() { ... },
                AuthenticationData = new AuthenticationData()
                {
                    AttemptAuthentication = AuthenticationData.AttemptAuthenticationEnum.Always,
                    // Add the following line for Native 3DS2:
                    //ThreeDSRequestData = new ThreeDSRequestData()
                    //{
                    //    NativeThreeDS = ThreeDSRequestData.NativeThreeDSEnum.Preferred
                    //}
                },
                Origin = _urlService.GetHostUrl(),
                BrowserInfo = request.BrowserInfo,
                ShopperIP = HttpContext.Connection.RemoteIpAddress?.ToString(),
                PaymentMethod = request.PaymentMethod
            };

            try
            {
                var res = await _paymentsService.PaymentsAsync(paymentRequest, cancellationToken: cancellationToken);
                _logger.LogInformation($"Response for Payment:\n{res}\n");
                return res.Ok();
            }
            catch (Adyen.HttpClient.HttpClientException e)
            {
                _logger.LogError($"Request for Payment failed:\n{e.ResponseBody}\n");
                throw;
            }
        }

        [HttpPost("api/submitAdditionalDetails")]
        public async Task<ActionResult<PaymentDetailsResponse>> SubmitAdditionalDetails(PaymentDetailsRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                var res = await _paymentsService.PaymentsDetailsAsync(request, cancellationToken: cancellationToken);
                _logger.LogInformation($"Response for PaymentDetails:\n{res}\n");
                return res.Ok();
            }
            catch (Adyen.HttpClient.HttpClientException e)
            {
                _logger.LogError($"Request for PaymentDetails failed:\n{e.ResponseBody}\n");
                throw;
            }
        }

        [HttpGet("api/handleShopperRedirect")]
        public async Task<IActionResult> HandleShoppperRedirect(string payload = null, string redirectResult = null, CancellationToken cancellationToken = default)
        {
            var detailsRequest = new PaymentDetailsRequest();
            if (!string.IsNullOrWhiteSpace(redirectResult))
            {
                // For redirect, you are redirected to an Adyen domain to complete the 3DS2 challenge.
                // After completing the 3DS2 challenge, you get the redirect result from Adyen in the returnUrl.
                // We then pass on the redirectResult.
                detailsRequest.Details = new PaymentCompletionDetails() { RedirectResult = redirectResult };
            }

            if (!string.IsNullOrWhiteSpace(payload))
            {
                detailsRequest.Details = new PaymentCompletionDetails() { Payload = payload };
            }

            try
            {
                var res = await _paymentsService.PaymentsDetailsAsync(detailsRequest, cancellationToken: cancellationToken);
                _logger.LogInformation($"Response for PaymentDetails:\n{res}\n");
                string redirectUrl = "/result/";

                var a = res.TryDeserializeOkResponse(out var result);

                if (result.ResultCode == PaymentDetailsResponse.ResultCodeEnum.Authorised)
                {
                    redirectUrl += "success";
                }
                else if (result.ResultCode == PaymentDetailsResponse.ResultCodeEnum.Pending)
                {
                    redirectUrl += "pending";
                }
                else if (result.ResultCode == PaymentDetailsResponse.ResultCodeEnum.Received)
                {
                    redirectUrl += "pending";
                }
                else if (result.ResultCode == PaymentDetailsResponse.ResultCodeEnum.Received)
                {
                    redirectUrl += "failed";
                }

                return Redirect(redirectUrl + "?reason=" + result.ResultCode);
            }
            catch (Adyen.HttpClient.HttpClientException e)
            {
                _logger.LogError($"Request for PaymentDetails failed:\n{e.ResponseBody}\n");
                throw;
            }
        }
    }
}