﻿using Login.Enums;
using Login.Services;
using Microsoft.Extensions.Logging;
using Prism.Events;
using Server.Events;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Weather.Services;

namespace Server.Services
{
    public class ServerService : IServerService
    {
        private readonly IWeatherService _weatherService;
        private readonly ILoginService _loginService;
        private readonly ILogger<ServerService> _logger;
        private readonly IEventAggregator _eventAggregator;
        private readonly ServerConfiguration _serverConfiguration;

        private bool badCredentials = false;

        private readonly string enterLocationMessage = "Enter location (Only english letters, exit to disconnect): ";
        private readonly string nonAsciiCharsMessage = "Non ASCII char detected (use only english letters, exit to disconnect), try again";
        private readonly string fethcingDataFromAPIMessage = "Fetching data from API";
        private readonly string enterLoginMessage = "Login: ";
        private readonly string enterPasswordMessage = "Password: ";
        private readonly string enterTimePeriodMessage = "Enter days count for weather forecast (1 - 6) or date (eg. 30-11-2020): ";
        private readonly string wrongTimePeriodMessage = "Incorrect weather period, try again";
        private readonly string registerMessage = "Account not found, do you want to create new account? (Y/N): ";
        private readonly string badPasswordMessage = "Bad password, try again";

        public ServerService(IWeatherService weatherService, ServerConfiguration serverConfiguration,
            ILoginService loginService, ILogger<ServerService> logger, IEventAggregator eventAggregator)
        {
            _weatherService = weatherService;
            _loginService = loginService;
            _serverConfiguration = serverConfiguration;
            _logger = logger;
            _eventAggregator = eventAggregator;
        }

        /// <summary>
        /// Checks if given server configuration is correct
        /// </summary>
        /// <returns>True or false</returns>
        private (bool result, string message) IsServerConfigurationCorrect(string ipAddress, int port)
        {
            string wrongServerConfigurationMessage = string.Empty;

            try
            {
                if (port < 1024 || port > 65535)
                {
                    wrongServerConfigurationMessage += "- Port number is wrong\n";
                }

                if (_serverConfiguration.WeatherBufferSize != 85)
                {
                    wrongServerConfigurationMessage += "- Buffer size is incorrect\n";
                }

                IPAddress.Parse(ipAddress);

                if (wrongServerConfigurationMessage.Length > 0)
                {
                    throw new Exception();
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                if (ex is FormatException || ex is ArgumentNullException)
                {
                    wrongServerConfigurationMessage += "- Server ip address is wrong\n";
                }

                _logger.LogDebug($"Server configuration message: {wrongServerConfigurationMessage}");

                return (false, wrongServerConfigurationMessage);
            }
        }

        /// <summary>
        /// Opeartes weather communication
        /// </summary>
        /// <param name="stream">client stream</param>
        /// <param name="buffer">buffer for weather data</param>
        /// <returns>string containing current state</returns>
        private async Task<string> ProcessWeatherCommunication(NetworkStream stream, byte[] buffer)
        {
            try
            {
                string location = Encoding.ASCII.GetString(buffer);

                if (location.IndexOf("exit") >= 0)
                {
                    return "exit";
                }
                else if (location.IndexOf("change") >= 0)
                {
                    await HandlePasswordChange(stream);

                    Array.Clear(buffer, 0, buffer.Length);

                    await stream.ReadAsync(buffer, 0, _serverConfiguration.WeatherBufferSize);

                    return "ok";
                }

                else if (location.IndexOf("??") < 0)
                {
                    if (location.IndexOf("\r\n") < 0)
                    {
                        location = new string(location.Where(c => c != '\0').ToArray());

                        var daysPeriodBuffer = new byte[_serverConfiguration.WeatherBufferSize];

                        int days = await GetWeatherPeriod(stream, daysPeriodBuffer);

                        await stream.WriteAsync(Encoding.ASCII.GetBytes(fethcingDataFromAPIMessage), 0, fethcingDataFromAPIMessage.Length);

                        _eventAggregator.GetEvent<ServerLogsChanged>().Publish($"Weather forecast requested for {location} for {days} day(s)");

                        string weatherData = await _weatherService.GetWeather(location, days);

                        _logger.LogInformation($"Weather for location: {location} for {days} days: \n {weatherData}\n");

                        byte[] weather = Encoding.ASCII.GetBytes(weatherData);

                        await stream.WriteAsync(weather, 0, weather.Length);

                        await stream.WriteAsync(Encoding.ASCII.GetBytes(enterLocationMessage), 0, enterLocationMessage.Length);
                    }

                    Array.Clear(buffer, 0, buffer.Length);
                }

                else if (location.IndexOf("??") >= 0)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(nonAsciiCharsMessage), 0, nonAsciiCharsMessage.Length);

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(enterLocationMessage), 0, enterLocationMessage.Length);

                    _logger.LogInformation("Non ASCII char detected in location");

                    Array.Clear(buffer, 0, buffer.Length);
                }

                await stream.ReadAsync(buffer, 0, _serverConfiguration.WeatherBufferSize);

                return "ok";
            }
            catch
            {
                return "exit";
            }
        }

        /// <summary>
        /// Gets correct weather period from client
        /// </summary>
        /// <param name="stream">client stream</param>
        /// <returns>dates period eg. 3</returns>
        private async Task<int> GetWeatherPeriod(NetworkStream stream, byte[] daysPeriodBuffer)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(enterTimePeriodMessage), 0, enterTimePeriodMessage.Length);

            do
            {
                Array.Clear(daysPeriodBuffer, 0, daysPeriodBuffer.Length);
                await stream.ReadAsync(daysPeriodBuffer, 0, daysPeriodBuffer.Length);
            } while (Encoding.ASCII.GetString(daysPeriodBuffer).Contains("\r\n"));

            string weatherDate = Encoding.ASCII.GetString(daysPeriodBuffer);

            int days;

            if (weatherDate.Contains('-'))
            {
                DateTime date;

                if (DateTime.TryParse(weatherDate, out date))
                {
                    var currentDate = Convert.ToDateTime(DateTime.Now.ToShortDateString());
                    days = (int)(date - currentDate).TotalDays + 1;
                }
                else
                {
                    days = -1;
                }
            }
            else
            {
                if (!int.TryParse(weatherDate, out days))
                {
                    days = -1;
                }
            }

            while (days < 1 || days > 6)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(wrongTimePeriodMessage), 0, wrongTimePeriodMessage.Length);

                await stream.WriteAsync(Encoding.ASCII.GetBytes(enterTimePeriodMessage), 0, enterTimePeriodMessage.Length);

                do
                {
                    Array.Clear(daysPeriodBuffer, 0, daysPeriodBuffer.Length);
                    await stream.ReadAsync(daysPeriodBuffer, 0, daysPeriodBuffer.Length);
                } while (Encoding.ASCII.GetString(daysPeriodBuffer).Contains("\r\n"));

                weatherDate = Encoding.ASCII.GetString(daysPeriodBuffer);

                if (weatherDate.Contains('-'))
                {
                    DateTime date;

                    if (DateTime.TryParse(weatherDate, out date))
                    {
                        var currentDate = Convert.ToDateTime(DateTime.Now.ToShortDateString());
                        days = (int)(date - currentDate).TotalDays;
                    }
                    else
                    {
                        days = -1;
                    }
                }
                else
                {
                    if (!int.TryParse(weatherDate, out days))
                    {
                        days = -1;
                    }
                }
            }

            return days;
        }

        /// <summary>
        /// Gets login from user
        /// </summary>
        /// <param name="stream">client stream</param>
        /// <param name="buffer">buffer for weather data</param>
        /// <returns>Login from user</returns>
        private async Task<string> GetLoginString(NetworkStream stream, byte[] buffer)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(enterLoginMessage), 0, enterLoginMessage.Length);

            Array.Clear(buffer, 0, buffer.Length);

            await stream.ReadAsync(buffer, 0, buffer.Length);

            return Encoding.ASCII.GetString(buffer).Replace("\0", "");
        }

        /// <summary>
        /// Gets password from user
        /// </summary>
        /// <param name="stream">client stream</param>
        /// <param name="buffer">buffer for weather data</param>
        /// <param name="data">current string for sign in</param>
        /// <returns>Password from user</returns>
        private async Task<string> GetPasswordString(NetworkStream stream, byte[] buffer)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(enterPasswordMessage), 0, enterPasswordMessage.Length);

            Array.Clear(buffer, 0, buffer.Length);

            await stream.ReadAsync(buffer, 0, buffer.Length);

            return Encoding.ASCII.GetString(buffer).Replace("\0", "");
        }

        private async Task HandleLogin(NetworkStream stream, byte[] signInBuffer, string login, string password)
        {
            badCredentials = false;

            var userCheck = await _loginService.CheckData(login, password);

            if (userCheck == UserLoginSettings.UserNotExists)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(registerMessage), 0, registerMessage.Length);

                await stream.ReadAsync(signInBuffer, 0, signInBuffer.Length);

                string response = Encoding.ASCII.GetString(signInBuffer);

                if (char.ToUpper(response[0]) == 'Y')
                {
                    await _loginService.RegisterAccount(login, password);

                    _eventAggregator.GetEvent<ServerLogsChanged>().Publish($"New user: {login} registered");

                    _logger.LogInformation($"New user: {login} registered");
                }
                else if (char.ToUpper(response[0]) == 'N')
                {
                    badCredentials = true;
                    await stream.ReadAsync(signInBuffer, 0, signInBuffer.Length);

                    return;
                }
            }
            else if (userCheck == UserLoginSettings.LoggedIn)
            {
                _eventAggregator.GetEvent<ServerLogsChanged>().Publish($"User: {login} logged in");

                _eventAggregator.GetEvent<UserLoggedInEvent>().Publish();

                _logger.LogInformation($"User: {login} logged in");
            }
            else
            {
                _eventAggregator.GetEvent<ServerLogsChanged>().Publish($"User: {login} bad password");
                _logger.LogInformation($"User: {login} bad password");

                badCredentials = true;

                await stream.WriteAsync(Encoding.ASCII.GetBytes(badPasswordMessage), 0, badPasswordMessage.Length);

                return;
            }

            string data = $"Welcome {login}\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(data), 0, data.Length);
        }

        private async Task HandlePasswordChange(NetworkStream stream)
        {
            byte[] changeBuffer = new byte[85];
            await stream.ReadAsync(changeBuffer, 0, changeBuffer.Length);
            string data = Encoding.ASCII.GetString(changeBuffer);
            data = data.Replace("\0", "");

            string login = data.Substring(0, data.IndexOf(';'));
            string password = data.Substring(data.IndexOf(';') + 1);

            if (await _loginService.ChangePassword(login, password))
            {
                _eventAggregator.GetEvent<ServerLogsChanged>().Publish($"User: {data.Substring(0, data.IndexOf(';'))} changed password");

                _logger.LogInformation($"User: {data.Substring(0, data.IndexOf(';'))} changed password");

                data = "Password changed\r\n";
            }
            else
            {
                _eventAggregator.GetEvent<ServerLogsChanged>().Publish($"Error while changing User: {data.Substring(0, data.IndexOf(';'))} password");

                _logger.LogInformation($"Error while changing User: {data.Substring(0, data.IndexOf(';'))} password");

                data = "Error password not changed\r\n";
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(data), 0, data.Length);
        }

        /// <summary>
        /// Start Tcp server
        /// </summary>
        /// <param name="ipAddress">Server ip address</param>
        /// <param name="port">Server port number</param>
        /// <returns>Task for tcp server</returns>
        public async Task StartServer(string ipAddress, int port)
        {
            var serverConfigurationResult = IsServerConfigurationCorrect(ipAddress, port);

            if (serverConfigurationResult.result)
            {
                TcpListener server = new TcpListener(IPAddress.Parse(ipAddress), port);

                server.Start();

                _eventAggregator.GetEvent<ServerLogsChanged>().Publish($"Starting Server at ipAddress: " +
                    $"{ipAddress}, port: {port}");

                _logger.LogInformation($"Starting Server at ipAddress: {ipAddress}, port: {port}");

                _eventAggregator.GetEvent<ServerStartedEvent>().Publish();

                while (true)
                {
                    TcpClient client = await server.AcceptTcpClientAsync();

                    _eventAggregator.GetEvent<ServerLogsChanged>().Publish("Client connected");

                    _logger.LogInformation("Client connected");

                    byte[] signInBuffer = new byte[_serverConfiguration.LoginBufferSize];
                    byte[] weatherBuffer = new byte[_serverConfiguration.WeatherBufferSize];

                    string data = string.Empty;

                    Task.Run(async () =>
                     {
                         do
                         {
                             var login = await GetLoginString(client.GetStream(), signInBuffer);

                             await client.GetStream().ReadAsync(signInBuffer, 0, 2);

                             var password = await GetPasswordString(client.GetStream(), signInBuffer);

                             await client.GetStream().ReadAsync(signInBuffer, 0, 2);

                             await HandleLogin(client.GetStream(), signInBuffer, login, password);

                         } while (badCredentials);

                         await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(enterLocationMessage), 0, enterLocationMessage.Length);

                         await client.GetStream().ReadAsync(weatherBuffer, 0, weatherBuffer.Length);

                         while (true)
                         {
                             if (await ProcessWeatherCommunication(client.GetStream(), weatherBuffer) == "exit")
                             {
                                 _logger.LogDebug("Client disconnected");

                                 _eventAggregator.GetEvent<UserDisconnectedEvent>().Publish();
                                 _eventAggregator.GetEvent<ServerLogsChanged>().Publish("Client disconnected");

                                 client.Close();
                             }
                         }
                     });
                }
            }

            else
            {
                _logger.LogInformation("Server configuration is wrong");

                _eventAggregator.GetEvent<WrongServerConfigurationEvent>().Publish(serverConfigurationResult.message);
            }
        }
    }
}
