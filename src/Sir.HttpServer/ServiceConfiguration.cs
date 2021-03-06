﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sir.HttpServer.Features;
using Sir.Search;
using System;
using System.IO;

namespace Sir.HttpServer
{
    public static class ServiceConfiguration
    {
        public static void RegisterComponents(
            IServiceCollection services, IServiceProvider container)
        {
            services.AddSingleton(typeof(JobQueue));
            services.AddSingleton(typeof(SaveAsJobQueue));
        }

        public static IServiceProvider Configure(IServiceCollection services)
        {
            var assemblyPath = Directory.GetCurrentDirectory();
            var config = new KeyValueConfiguration(Path.Combine(assemblyPath, "sir.ini"));

            services.Add(new ServiceDescriptor(typeof(IConfigurationProvider), config));

            var loggerFactory = services.BuildServiceProvider().GetService<ILoggerFactory>();
            var model = new TextModel();
            var sessionFactory = new SessionFactory(config, loggerFactory.CreateLogger<SessionFactory>());
            var qp = new QueryParser<string>(sessionFactory, model, loggerFactory.CreateLogger<QueryParser<string>>());
            var httpParser = new HttpStringQueryParser(qp);

            services.AddSingleton(typeof(ITextModel), model);
            services.AddSingleton(typeof(ISessionFactory), sessionFactory);
            services.AddSingleton(typeof(SessionFactory), sessionFactory);
            services.AddSingleton(typeof(QueryParser<string>), qp);
            services.AddSingleton(typeof(HttpStringQueryParser), httpParser);
            services.AddSingleton(typeof(IHttpWriter), new HttpWriter(sessionFactory));
            services.AddSingleton(typeof(IHttpReader), new HttpReader(
                sessionFactory,
                httpParser,
                config,
                loggerFactory.CreateLogger<HttpReader>()));

            return services.BuildServiceProvider();
        }
    }
}
