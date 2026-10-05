#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace SimpleTrans
{
    /// <summary>FileId 计算规则，服务端与客户端必须一致。</summary>
    public static class FileIdentity
    {
        /// <summary>
        /// 计算稳定 FileId：SHA256(name ‖ sha256) 的前 16 字节；
        /// 没有内容摘要时退化为 SHA256(name ‖ "len:" ‖ length)。
        /// </summary>
        public static Guid ComputeFileId(string name, byte[]? contentSha256, long length)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            using (var sha = SHA256.Create())
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                byte[] input;
                if (contentSha256 != null && contentSha256.Length == 32)
                {
                    input = new byte[nameBytes.Length + 32];
                    Buffer.BlockCopy(nameBytes, 0, input, 0, nameBytes.Length);
                    Buffer.BlockCopy(contentSha256, 0, input, nameBytes.Length, 32);
                }
                else
                {
                    byte[] lenBytes = Encoding.UTF8.GetBytes("len:" + length.ToString());
                    input = new byte[nameBytes.Length + lenBytes.Length];
                    Buffer.BlockCopy(nameBytes, 0, input, 0, nameBytes.Length);
                    Buffer.BlockCopy(lenBytes, 0, input, nameBytes.Length, lenBytes.Length);
                }

                byte[] digest = sha.ComputeHash(input);
                byte[] head = new byte[16];
                Buffer.BlockCopy(digest, 0, head, 0, 16);
                return new Guid(head);
            }
        }

        /// <summary>为文件源计算 FileId（服务端在构建清单时调用）。</summary>
        public static Guid ComputeFileId(IFileSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return ComputeFileId(source.Name, source.KnownSha256, source.Length);
        }
    }

    /// <summary>字节数组与十六进制字符串互转（netstandard2.1 无 Convert.ToHexString）。</summary>
    public static class Hex
    {
        private const string Digits = "0123456789abcdef";

        public static string ToHex(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = Digits[bytes[i] >> 4];
                chars[i * 2 + 1] = Digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }

        public static string ToHex(ReadOnlySpan<byte> bytes)
        {
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = Digits[bytes[i] >> 4];
                chars[i * 2 + 1] = Digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }
    }
}