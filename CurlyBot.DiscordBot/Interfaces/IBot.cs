using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CurlyBot.DiscordBot.Interfaces
{
    internal interface IBot
    {
        Task StartAsync();

        Task StopASync();
    }
}
