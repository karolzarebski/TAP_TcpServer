﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServerLibrary;
using ServerLibrary.Services;
using System;
using WeatherLibrary;
using WeatherLibrary.Services;

namespace TAP_TcpServer
{
    class Program
    {
        private static IConfiguration _configuration;
        private static ServiceProvider _serviceProvider;

        static void Main(string[] args)
        {
            try
            {
                _configuration = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json")
                    .Build();

                var servicesCollection = new ServiceCollection();
                ConfigureServices(servicesCollection);

                _serviceProvider = servicesCollection.BuildServiceProvider();

                _serviceProvider.GetRequiredService<IServerService>().Server().Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occured during application startup, {ex}");
            }
        }

        private static void ConfigureServices(IServiceCollection servicesCollection)
        {
            var weatherApiConfiguration = _configuration.GetSection("WeatherApi").Get<WeatherApiConfiguration>();
            var serverConfiguration = _configuration.GetSection("ServerConfiguration").Get<ServerConfiguration>();

            servicesCollection
                .AddSingleton(_configuration)
                .AddSingleton(weatherApiConfiguration)
                .AddSingleton(serverConfiguration)
                .AddSingleton<IWeatherService, WeatherService>()
                .AddSingleton<IServerService, ServerService>();
        }
    }
}
