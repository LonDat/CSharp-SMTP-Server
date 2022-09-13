﻿using System;
using System.Text;
using System.Threading.Tasks;
using CSharp_SMTP_Server.Misc;
using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol.Responses;
using static System.FormattableString;

namespace CSharp_SMTP_Server.Protocol.Commands
{
	internal class TransactionCommands
	{
		internal static async Task ProcessCommand(ClientProcessor processor, string command, string data)
		{
			switch (command)
			{
				case "RSET":
					processor.Transaction = null;
                    await processor.WriteCode(250, "2.1.5", "Flushed");
					break;

				case "MAIL FROM":
					{
						var address = ProcessAddress(data);
						if (address == null) await processor.WriteCode(501, "5.5.2");
						else
						{
							if (processor.Server.Filter != null)
							{
								var result = await processor.Server.Filter.IsAllowedSender(address, processor.RemoteEndPoint);

								if (result.Type != SmtpResultType.Success)
								{
									await processor.WriteCode(554,
										result.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
										string.IsNullOrWhiteSpace(result.FailMessage)
											? "Delivery not authorized, message refused"
											: result.FailMessage);
									return;
								}
							}

							processor.Transaction = new MailTransaction(address)
							{
								RemoteEndPoint = processor.RemoteEndPoint,
								Encryption = processor.Encryption
							};
							await processor.WriteCode(250, "2.0.0");
						}
					}
					break;
					
				case "RCPT TO":
					{
						if (processor.Transaction == null)
						{
							await processor.WriteCode(503, "5.5.1", "MAIL FROM first.");
							return;
						}

						var address = ProcessAddress(data);
						if (address == null) await processor.WriteCode(501);
						else
						{
                            if (processor.Server.Options.RecipientsLimit > 0 && processor.Server.Options.RecipientsLimit <= processor.Transaction.DeliverTo.Count)
                            {
                                await processor.WriteCode(550, "5.5.3", "Too many recipients");
                                return;
                            }

							if (processor.Server.Filter != null)
							{
								var filterResult = await processor.Server.Filter.CanDeliver(processor.Transaction.From,address, !string.IsNullOrEmpty(processor.Username), processor.Username, processor.RemoteEndPoint);

								if (filterResult.Type != SmtpResultType.Success)
								{
									await processor.WriteCode(550,
										filterResult.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
										string.IsNullOrWhiteSpace(filterResult.FailMessage)
											? "Delivery not authorized, message refused"
											: filterResult.FailMessage);
									return;
								}
							}

							var result = await processor.Server.MailDeliveryInterface.DoesUserExist(address);

							switch (result)
							{
								case UserExistsCodes.BadDestinationMailboxAddress:
									await processor.WriteCode(550, "5.1.1", "Requested action not taken: Bad destination mailbox address");
									return;

								case UserExistsCodes.BadDestinationSystemAddress:
									await processor.WriteCode(550, "5.1.2", "Requested action not taken: Bad destination system address");
									return;

								case UserExistsCodes.DestinationMailboxAddressAmbiguous:
									await processor.WriteCode(550, "5.1.4", "Requested action not taken: Destination mailbox address ambiguous");
									return;

								case UserExistsCodes.DestinationAddressHasMovedAndNoForwardingAddress:
									await processor.WriteCode(550, "5.1.6", "Requested action not taken: Destination mailbox has moved, No forwarding address");
									return;

								case UserExistsCodes.BadSendersSystemAddress:
									await processor.WriteCode(550, "5.1.8", "Requested action not taken: Bad sender's mailbox address syntax");
									return;

								default:
									processor.Transaction.DeliverTo.Add(address);
									await processor.WriteCode(250, "2.1.5");
									break;
							}	
						}
					}
					break;

				case "DATA":
					if (processor.Transaction == null || processor.Transaction.DeliverTo.Count == 0)
					{
						await processor.WriteCode(503, "5.5.1", "RCPT TO first.");
						return;
					}

					processor.DataBuilder = new StringBuilder();
                    processor.Counter = 0;
                    processor.CaptureData = 1;
					await processor.WriteCode(354);
					break;
			}
		}

		internal static async Task ProcessData(ClientProcessor processor, string data)
		{
			data = data.Replace("\r", "");
			var dta = data.Split('\n');
			foreach (var dt in dta)
			{
				if (dt == ".")
				{
					processor.CaptureData = 0;
					processor.Transaction!.Body = processor.DataBuilder!.ToString();

                    if (processor.Server.Options.MessageCharactersLimit != 0 &&
                        processor.Server.Options.MessageCharactersLimit < processor.Counter)
                    {
                        processor.Transaction = null;
                        await processor.WriteCode(552, "5.4.3", "Message size exceeds the administrative limit.");
                        return;
                    }

                    string received = string.Empty;
                    
                    if (!string.IsNullOrEmpty(processor.Username)) processor.Transaction.AuthenticatedUser = processor.Username;
                    else received = $"from {(processor.RemoteEndPoint == null ? "unknown" : processor.RemoteEndPoint.Address.ToString())} ";

                    received += Invariant($"by {processor.Server.Options.ServerName} with SMTP; {DateTime.UtcNow:ddd, dd MMM yyyy HH:mm:ss} +0000 (UTC)");
                    
                    EmailParser.AddHeader("Received", received, ref processor.Transaction.Body);
                    processor.Transaction.Headers = EmailParser.ParseHeaders(processor.Transaction.Body);

                    if (processor.Server.Filter != null)
					{
						var filterResult = await processor.Server.Filter.CanProcessTransaction(processor.Transaction);

						if (filterResult.Type != SmtpResultType.Success)
						{
                            processor.Transaction = null;
                            await processor.WriteCode(554,
								filterResult.Type == SmtpResultType.PermanentFail ? "5.7.1" : "4.7.1",
								string.IsNullOrWhiteSpace(filterResult.FailMessage)
									? "Delivery not authorized, message refused"
									: filterResult.FailMessage);
							return;
						}
					}

                    var delivery = (MailTransaction)processor.Transaction.Clone();
                    processor.Transaction = null;

                    _ = processor.Server.DeliverMessage(delivery);

					await processor.WriteCode(250, "2.3.0");
					return;
				}

                processor.Counter += (ulong)dt.Length;
                if (processor.Server.Options.MessageCharactersLimit == 0 ||
                    processor.Server.Options.MessageCharactersLimit >= processor.Counter)
                {
                    processor.DataBuilder!.AppendLine(dt);
                }
            }
		}

		private static string? ProcessAddress(string data)
		{
			if (!data.Contains('<', StringComparison.Ordinal) || !data.Contains('>', StringComparison.Ordinal)) return null;

			var address = data[(data.IndexOf("<", StringComparison.Ordinal) + 1)..];
			address = address[..address.IndexOf(">", StringComparison.Ordinal)];

			return string.IsNullOrWhiteSpace(address) ? null : address;
		}
	}
}
