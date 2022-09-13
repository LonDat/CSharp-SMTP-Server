﻿using System;
using System.Collections.Generic;
using System.Net;
using CSharp_SMTP_Server.Networking;

namespace CSharp_SMTP_Server
{
	public class MailTransaction : ICloneable
	{
		public MailTransaction(string from)
		{
			From = from;
			DeliverTo = new List<string>();
			AuthenticatedUser = null;
		}

		public readonly string From;
		public string? Body;

		public string? Subject => TryGetHeader("Subject", out var value) ? value : null;

		/// <summary>
		/// Recipients specified in the transaction.
		/// </summary>
		public List<string> DeliverTo;
		
		/// <summary>
		/// Recipients specified in the header (To).
		/// </summary>
		public IEnumerable<string> To => ParseAddresses("To");
		
		/// <summary>
		/// Recipients specified in the header (CC).
		/// </summary>
		public IEnumerable<string> Cc => ParseAddresses("cc");
		
		/// <summary>
		/// Recipients specified in the header (BCC).
		/// </summary>
		public IEnumerable<string> Bcc => ParseAddresses("bcc");
		
		private IEnumerable<string> ParseAddresses(string header)
		{
			if (!TryGetHeader(header, out var t)) yield break;
			
			while (t!.Contains('<', StringComparison.Ordinal))
			{
				if (!t.Contains('>', StringComparison.Ordinal)) yield break;
				var address = t[(t.IndexOf("<", StringComparison.Ordinal) + 1)..];
				var i = address.IndexOf(">", StringComparison.Ordinal);
				yield return address[..i];
				if (i + 1 >= t.Length) yield break;
				t = t.Substring(i + 1);
			}
		}

		private bool TryGetHeader(string header, out string? value)
		{
			value = null;
			
			if (Headers == null || !Headers.TryGetValue(header, out var tt))
				return false;

			if (tt.Count != 1)
				return false;

			value = tt[0];
			return value != null;
		}

		public Dictionary<string, List<string>>? Headers;
		
		public EndPoint? RemoteEndPoint;

		/// <summary>
		/// Username of authenticated users. Empty if user is not authenticated.
		/// </summary>
		public string? AuthenticatedUser;

		public ConnectionEncryption Encryption;

		public object Clone()
		{
			return new MailTransaction(From)
			{
				AuthenticatedUser = AuthenticatedUser,
				Body = Body,
				Headers = Headers,
				RemoteEndPoint = RemoteEndPoint,
				DeliverTo = DeliverTo
			};
		}
	}
}
