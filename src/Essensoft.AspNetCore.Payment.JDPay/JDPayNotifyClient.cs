﻿using Essensoft.AspNetCore.Payment.JDPay.Parser;
using Essensoft.AspNetCore.Payment.JDPay.Utility;
using Essensoft.AspNetCore.Payment.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Essensoft.AspNetCore.Payment.JDPay
{
    public class JDPayNotifyClient : IJDPayNotifyClient
    {
        private const string SIGN = "sign";
        private AsymmetricKeyParameter PrivateKey;
        private AsymmetricKeyParameter PublicKey;
        private byte[] DesKey;

        public JDPayOptions Options { get; set; }

        public virtual ILogger Logger { get; set; }

        #region JDPayNotifyClient Constructors

        public JDPayNotifyClient(
            IOptions<JDPayOptions> optionsAccessor,
            ILogger<JDPayNotifyClient> logger)
        {
            Options = optionsAccessor.Value;
            Logger = logger;

            if (string.IsNullOrEmpty(Options.Merchant))
            {
                throw new ArgumentNullException(nameof(Options.Merchant));
            }

            if (string.IsNullOrEmpty(Options.RsaPrivateKey))
            {
                throw new ArgumentNullException(nameof(Options.RsaPrivateKey));
            }

            if (string.IsNullOrEmpty(Options.RsaPublicKey))
            {
                throw new ArgumentNullException(nameof(Options.RsaPublicKey));
            }

            if (string.IsNullOrEmpty(Options.DesKey))
            {
                throw new ArgumentNullException(nameof(Options.DesKey));
            }

            PrivateKey = RSAUtilities.GetKeyParameterFormPrivateKey(Options.RsaPrivateKey);
            PublicKey = RSAUtilities.GetKeyParameterFormPublicKey(Options.RsaPublicKey);
            DesKey = Convert.FromBase64String(Options.DesKey);
        }

        public JDPayNotifyClient(IOptions<JDPayOptions> optionsAccessor)
            : this(optionsAccessor, null)
        { }

        #endregion

        #region IJDPayNotifyClient Members

        public async Task<T> ExecuteAsync<T>(HttpRequest request) where T : JDPayNotifyResponse
        {
            if (request.HasFormContentType)
            {
                var parameters = await GetParametersAsync(request);

                var query = HttpClientEx.BuildQuery(parameters);
                Logger?.LogTrace(0, "Request:{query}", query);

                var parser = new JDPayDictionaryParser<T>();
                var rsp = parser.Parse(parameters);

                CheckNotifySign(rsp.Parameters);
                return rsp;
            }
            else if (request.HasTextXmlContentType())
            {
                var body = await new StreamReader(request.Body).ReadToEndAsync();
                Logger?.LogTrace(0, "Request:{body}", body);

                var parser = new JDPayXmlParser<T>();
                var rsp = parser.Parse(JDPayUtility.FotmatXmlString(body));
                if (!string.IsNullOrEmpty(rsp.Encrypt))
                {
                    var encrypt = rsp.Encrypt;
                    var base64EncryptStr = Encoding.UTF8.GetString(Convert.FromBase64String(encrypt));
                    var reqBody = JDPaySecurity.DecryptECB(base64EncryptStr, DesKey);
                    Logger?.LogTrace(1, "Encrypt Content:{reqBody}", reqBody);

                    var reqBodyDoc = new XmlDocument();
                    reqBodyDoc.LoadXml(reqBody);

                    var sign = JDPayUtility.GetValue(reqBodyDoc, "sign");
                    var rootNode = reqBodyDoc.SelectSingleNode("jdpay");
                    var signNode = rootNode.SelectSingleNode("sign");
                    rootNode.RemoveChild(signNode);

                    var reqBodyStr = JDPayUtility.ConvertXmlToString(reqBodyDoc);
                    var xmlh = rsp.Body.Substring(0, rsp.Body.IndexOf("<jdpay>"));
                    if (!string.IsNullOrEmpty(xmlh))
                    {
                        reqBodyStr = reqBodyStr.Replace("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xmlh);
                    }
                    var sha256SourceSignString = SHA256.Compute(reqBodyStr);
                    var decryptByte = RSA_ECB_PKCS1Padding.Decrypt(Convert.FromBase64String(sign), PublicKey);
                    var decryptStr = JDPaySecurity.BytesToString(decryptByte);
                    if (sha256SourceSignString == decryptStr)
                    {
                        rsp = parser.Parse(reqBody);
                        rsp.Encrypt = encrypt;
                        return rsp;
                    }
                    else
                    {
                        throw new Exception("sign check fail: check Sign and Data Fail!");
                    }
                }
                else
                {
                    throw new Exception("encrypt is Empty!");
                }
            }
            else
            {
                throw new Exception("Content type is not supported!");
            }
        }

        #endregion

        #region Common Method

        private async Task<JDPayDictionary> GetParametersAsync(HttpRequest request)
        {
            var parameters = new JDPayDictionary();
            var form = await request.ReadFormAsync();
            foreach (var item in form)
            {
                if (!string.IsNullOrEmpty(item.Value))
                {
                    parameters.Add(item.Key, item.Key == SIGN ? item.Value.ToString() : JDPaySecurity.DecryptECB(item.Value, DesKey));
                }
            }
            return parameters;
        }

        private void CheckNotifySign(JDPayDictionary parameters)
        {
            if (parameters.Count == 0)
            {
                throw new Exception("sign check fail: parameters is Empty!");
            }

            if (!parameters.TryGetValue("sign", out var sign))
            {
                throw new Exception("sign check fail: sign is Empty!");
            }

            var signContent = JDPaySecurity.GetSignContent(parameters);
            if (!JDPaySecurity.RSACheckContent(signContent, sign, PublicKey))
            {
                throw new Exception("sign check fail: check Sign and Data Fail");
            }
        }

        #endregion
    }
}
