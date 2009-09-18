﻿#region

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Rnwood.SmtpServer.Extensions;
using Rnwood.SmtpServer.Verbs;

#endregion

namespace Rnwood.SmtpServer
{
    public class ConnectionProcessor : IConnectionProcessor
    {
        private readonly Encoding _currentReaderEncoding;
        private readonly Encoding _sevenBitASCIIEncoding;
        private readonly TcpClient _tcpClient;
        private StreamReader _reader;

        private Stream _stream;
        private StreamWriter _writer;

        public ConnectionProcessor(Server server, TcpClient tcpClient)
        {
            VerbMap = new VerbMap();
            Session = new Session()
                          {
                              ClientAddress = ((IPEndPoint) tcpClient.Client.RemoteEndPoint).Address,
                              StartDate = DateTime.Now
                          };

            Server = server;
            _tcpClient = tcpClient;
            _tcpClient.ReceiveTimeout = Server.Behaviour.GetReceiveTimeout(this);

            _currentReaderEncoding =
                _sevenBitASCIIEncoding =
                Encoding.GetEncoding("ASCII", new EncoderExceptionFallback(), new ASCIITruncatingDecoderFallback());
            _stream = tcpClient.GetStream();

            SetupReaderAndWriter();
            SetupVerbs();
        }

        #region IConnectionProcessor Members

        public Server Server { get; private set; }

        public void SwitchReaderEncoding(Encoding encoding)
        {
            SetupReaderAndWriter();
        }

        public void SwitchReaderEncodingToDefault()
        {
            SwitchReaderEncoding(_sevenBitASCIIEncoding);
        }

        public ExtensionProcessor[] ExtensionProcessors { get; private set; }

        public void CloseConnection()
        {
            _writer.Flush();
            _tcpClient.Close();
        }

        public VerbMap VerbMap { get; private set; }

        public void ApplyStreamFilter(Func<Stream, Stream> filter)
        {
            _stream = filter(_stream);
            SetupReaderAndWriter();
        }

        public MailVerb MailVerb
        {
            get { return (MailVerb) VerbMap.GetVerbProcessor("MAIL"); }
        }

        public void WriteLine(string text, params object[] arg)
        {
            string formattedText = string.Format(text, arg);
            Session.AppendToLog(formattedText);
            _writer.WriteLine(formattedText);
        }

        public void WriteResponse(SmtpResponse response)
        {
            WriteLine(response.ToString().TrimEnd());
        }

        public string ReadLine()
        {
            string text = _reader.ReadLine();
            Session.AppendToLog(text);
            return text;
        }

        public Session Session { get; private set; }

        public Message CurrentMessage { get; private set; }

        public Message NewMessage()
        {
            CurrentMessage = new Message(Session);
            return CurrentMessage;
        }

        public void CommitMessage()
        {
            Message message = CurrentMessage;
            Session.Messages.Add(message);
            CurrentMessage = null;

            Server.Behaviour.OnMessageReceived(message);
        }

        public void AbortMessage()
        {
            CurrentMessage = null;
        }

        #endregion

        private void SetupReaderAndWriter()
        {
            _writer = new StreamWriter(_stream, _currentReaderEncoding) {AutoFlush = true, NewLine = "\r\n"};
            _reader = new StreamReader(_stream, _currentReaderEncoding);
        }

        private void SetupVerbs()
        {
            VerbMap.SetVerbProcessor("HELO", new HeloVerb());
            VerbMap.SetVerbProcessor("EHLO", new EhloVerb());
            VerbMap.SetVerbProcessor("QUIT", new QuitVerb());
            VerbMap.SetVerbProcessor("MAIL", new MailVerb());
            VerbMap.SetVerbProcessor("RCPT", new RcptVerb());
            VerbMap.SetVerbProcessor("DATA", new DataVerb());
            VerbMap.SetVerbProcessor("RSET", new RsetVerb());
            VerbMap.SetVerbProcessor("NOOP", new NoopVerb());

            ExtensionProcessors =
                Server.Behaviour.GetExtensions(this).Select(e => e.CreateExtensionProcessor(this)).ToArray();
        }

        public void Start()
        {
            try
            {
                Server.Behaviour.OnSessionStarted(this, Session);

                if (Server.Behaviour.RunOverSSL)
                {
                    SslStream sslStream = new SslStream(_stream);
                    sslStream.AuthenticateAsServer(Server.Behaviour.GetSSLCertificate(this));
                    _stream = sslStream;
                    Session.SecureConnection = true;
                }

                WriteResponse(new SmtpResponse(StandardSmtpResponseCode.ServiceReady,
                                               Server.Behaviour.DomainName + " smtp4dev ready"));

                while (_tcpClient.Client.Connected)
                {
                    SmtpCommand command = new SmtpCommand(ReadLine());
                    Server.Behaviour.OnCommandReceived(this, command);

                    if (command.IsValid)
                    {
                        Verb verbProcessor = VerbMap.GetVerbProcessor(command.Verb);

                        if (verbProcessor != null)
                        {
                            try
                            {
                                verbProcessor.Process(this, command);
                            }
                            catch (SmtpServerException exception)
                            {
                                WriteResponse(exception.SmtpResponse);
                            }
                        }
                        else
                        {
                            WriteResponse(new SmtpResponse(StandardSmtpResponseCode.SyntaxErrorCommandUnrecognised,
                                                           "Command unrecognised"));
                        }
                    }
                    else if (command.IsEmpty)
                    {
                    }
                    else
                    {
                        WriteResponse(new SmtpResponse(StandardSmtpResponseCode.SyntaxErrorCommandUnrecognised,
                                                       "Command unrecognised"));
                    }
                }
            }
            catch (IOException ioException)
            {
                Session.SessionError = ioException.Message;
            }

            CloseConnection();

            Session.EndDate = DateTime.Now;
            Server.Behaviour.OnSessionCompleted(Session);
        }
    }
}