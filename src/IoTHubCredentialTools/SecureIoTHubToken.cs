﻿
using System;
using System.IO;
using System.Collections;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using System.Text;

namespace IoTHubCredentialTools
{
    public class SecureIoTHubToken
    {
        public static string Read(string name)
        {
            // load an existing key from a no-expired cert with the subject name passed in from the OS-provided X509Store
            using (X509Store store = new X509Store("IoTHub", StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);
                foreach (X509Certificate2 cert in store.Certificates)
                {
                    if ((cert.SubjectName.Decode(X500DistinguishedNameFlags.None | X500DistinguishedNameFlags.DoNotUseQuotes).Equals("CN=" + name, StringComparison.OrdinalIgnoreCase)) &&
                        (DateTime.Now < cert.NotAfter))
                    {
                        using (RSA rsa = cert.GetRSAPrivateKey())
                        {
                            if (rsa != null)
                            {
                                foreach (System.Security.Cryptography.X509Certificates.X509Extension extension in cert.Extensions)
                                {
                                    // check for instruction code extension
                                    if ((extension.Oid.Value == "2.5.29.23") && (extension.RawData.Length >= 4))
                                    {
                                        byte[] bytes = new byte[extension.RawData.Length - 4];
                                        Array.Copy(extension.RawData, 4, bytes, 0, bytes.Length);
                                        byte[] token = rsa.Decrypt(bytes, RSAEncryptionPadding.OaepSHA1);
                                        return Encoding.ASCII.GetString(token);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public static void Write(string name, string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException("Token not found in X509Store and no new token provided!");
            }

            SecureRandom random = new SecureRandom();
            KeyGenerationParameters keyGenerationParameters = new KeyGenerationParameters(random, 2048);
            RsaKeyPairGenerator keyPairGenerator = new RsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenerationParameters);
            AsymmetricCipherKeyPair keys = keyPairGenerator.GenerateKeyPair();

            ArrayList nameOids = new ArrayList();
            nameOids.Add(X509Name.CN);
            ArrayList nameValues = new ArrayList();
            nameValues.Add(name);
            X509Name subjectDN = new X509Name(nameOids, nameValues);
            X509Name issuerDN = subjectDN;

            X509V3CertificateGenerator cg = new X509V3CertificateGenerator();
            cg.SetSerialNumber(BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random));
            cg.SetIssuerDN(issuerDN);
            cg.SetSubjectDN(subjectDN);
            cg.SetNotBefore(DateTime.Now);
            cg.SetNotAfter(DateTime.Now.AddMonths(12));
            cg.SetPublicKey(keys.Public);

            // encrypt the token with the public key so only the owner of the assoc. private key can decrypt it and
            // "hide" it in the instruction code cert extension
            RSA rsa = RSA.Create();
            RSAParameters rsaParams = new RSAParameters();
            RsaKeyParameters keyParams = (RsaKeyParameters)keys.Public;

            rsaParams.Modulus = new byte[keyParams.Modulus.ToByteArrayUnsigned().Length];
            keyParams.Modulus.ToByteArrayUnsigned().CopyTo(rsaParams.Modulus, 0);

            rsaParams.Exponent = new byte[keyParams.Exponent.ToByteArrayUnsigned().Length];
            keyParams.Exponent.ToByteArrayUnsigned().CopyTo(rsaParams.Exponent, 0);

            rsa.ImportParameters(rsaParams);
            if (rsa != null)
            {
                byte[] bytes = rsa.Encrypt(Encoding.ASCII.GetBytes(connectionString), RSAEncryptionPadding.OaepSHA1);
                if (bytes != null)
                {
                    cg.AddExtension(X509Extensions.InstructionCode, false, bytes);
                }
                else
                {
                    rsa.Dispose();
                    throw new CryptographicException("Could not encrypt IoTHub security token using generated public key!");
                }
            }
            rsa.Dispose();
            
            // sign the cert with the private key
            ISignatureFactory signatureFactory = new Asn1SignatureFactory("SHA256WITHRSA", keys.Private, random);
            Org.BouncyCastle.X509.X509Certificate x509 = cg.Generate(signatureFactory);

            // create a PKCS12 store for the cert and its private key
            X509Certificate2 certificate = null;
            using (MemoryStream pfxData = new MemoryStream())
            {
                Pkcs12Store pkcsStore = new Pkcs12StoreBuilder().Build();
                X509CertificateEntry[] chain = new X509CertificateEntry[1];
                string passcode = "passcode";
                chain[0] = new X509CertificateEntry(x509);
                pkcsStore.SetKeyEntry(name, new AsymmetricKeyEntry(keys.Private), chain);
                pkcsStore.Save(pfxData, passcode.ToCharArray(), random);

                // create X509Certificate2 object from PKCS12 file
                certificate = CreateCertificateFromPKCS12(pfxData.ToArray(), passcode);

                // Add to X509Store
                using (X509Store store = new X509Store("IoTHub", StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);

                    // remove any existing cert with our name from the store
                    foreach (X509Certificate2 cert in store.Certificates)
                    {
                        if (cert.SubjectName.Decode(X500DistinguishedNameFlags.None | X500DistinguishedNameFlags.DoNotUseQuotes).Equals("CN=" + name, StringComparison.OrdinalIgnoreCase))
                        {
                            store.Remove(cert);
                        }
                    }

                    // add new one
                    store.Add(certificate);
                }
            }
        }

        private static X509Certificate2 CreateCertificateFromPKCS12(byte[] rawData, string password)
        {
            Exception ex = null;
            int flagsRetryCounter = 0;
            X509Certificate2 certificate = null;
            X509KeyStorageFlags[] storageFlags = {
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet
            };

            // try some combinations of storage flags, support is platform dependent
            while (certificate == null &&
                flagsRetryCounter < storageFlags.Length)
            {
                try
                {
                    // merge first cert with private key into X509Certificate2
                    certificate = new X509Certificate2(
                        rawData,
                        (password == null) ? String.Empty : password,
                        storageFlags[flagsRetryCounter]);
                    // can we really access the private key?
                    using (RSA rsa = certificate.GetRSAPrivateKey()) { }
                }
                catch (Exception e)
                {
                    ex = e;
                    certificate = null;
                }
                flagsRetryCounter++;
            }

            if (certificate == null)
            {
                throw new NotSupportedException("Creating X509Certificate from PKCS #12 store failed", ex);
            }

            return certificate;
        }
    }
}
