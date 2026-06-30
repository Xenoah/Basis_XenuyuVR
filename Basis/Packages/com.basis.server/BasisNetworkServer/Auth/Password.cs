#nullable enable

using Encoding = System.Text.Encoding;
using static Basis.Network.Core.Serializable.SerializableBasis;
using System;

namespace Basis.Network.Server.Auth
{

    /// `string` の newtype。server に設定された password を表す。
    internal readonly struct ServerPassword
    {
        public readonly string V { get; }
        public ServerPassword(string password) { V = password; }
    }

    /// `string` の newtype。user が送ってきた password を表す。
    internal readonly struct UserPassword
    {
        public readonly string V { get; }
        public UserPassword(string password) { V = password; }
    }

    internal readonly struct Deserialized
    {
        public readonly UserPassword Password { get; }
        public Deserialized(byte[] Bytesmsg)
        {
            string password = Encoding.UTF8.GetString(Bytesmsg);
            Password = new UserPassword(password);
        }
    }

    public class PasswordAuth : IAuth
    {
        private readonly ServerPassword serverPassword;

        /// `serverPassword` が空文字列の場合、server には password がなく、どの user でも接続できる。
        public PasswordAuth(string serverPassword)
        {
            this.serverPassword = new ServerPassword(serverPassword);
        }

        private static bool CheckPassword(ServerPassword serverPassword, UserPassword userPassword)
        {
            if (string.IsNullOrEmpty(serverPassword.V))
            {
                BNL.LogError("No server password set — the server is open to all users.");
                return true;
            }
            if (string.IsNullOrEmpty(userPassword.V))
            {
                BNL.Log("User had an empty password, user is rejected");
                return false;
            }
            byte[] serverBytes = Encoding.UTF8.GetBytes(serverPassword.V);
            byte[] userBytes = Encoding.UTF8.GetBytes(userPassword.V);
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(serverBytes, userBytes))
            {
                return true;
            }
            else
            {
                BNL.LogError("Passwords do not match, user is rejected");
                return false;
            }
        }

        public bool IsAuthenticated(byte[] Bytesmsg)
        {
            var deserialized = new Deserialized(Bytesmsg);
            return CheckPassword(serverPassword, deserialized.Password);
        }
    }
}
