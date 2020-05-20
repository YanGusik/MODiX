﻿#nullable enable

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Discord;
using Discord.WebSocket;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Modix.Common.Messaging;
using Modix.Data.Models.Core;
using Modix.Services.Core;
using Modix.Services.Utilities;

namespace Modix.Services.MessageLogging
{
    [ServiceBinding(ServiceLifetime.Scoped)]
    public class MessageLoggingBehavior
        : INotificationHandler<MessageDeletedNotification>,
            INotificationHandler<MessageUpdatedNotification>
    {
        public MessageLoggingBehavior(
            IDesignatedChannelService designatedChannelService,
            ILogger<MessageLoggingBehavior> logger,
            ISelfUserProvider selfUserProvider)
        {
            _designatedChannelService = designatedChannelService;
            _logger = logger;
            _selfUserProvider = selfUserProvider;
        }

        public async Task HandleNotificationAsync(
            MessageDeletedNotification notification,
            CancellationToken cancellationToken)
        {
            var guild = (notification.Channel as ISocketGuildChannel)?.Guild;

            using var logScope = MessageLoggingLogMessages.BeginMessageNotificationScope(_logger, guild?.Id, notification.Channel.Id, notification.Message.Id);

            MessageLoggingLogMessages.MessageDeletedHandling(_logger);

            await TryLogAsync(
                guild,
                notification.Message.HasValue ? notification.Message.Value : null,
                null,
                notification.Channel,
                () =>
                {
                    var content = $":wastebasket:Message Deleted in {MentionUtils.MentionChannel(notification.Channel.Id)} `{notification.Channel.Id}`";

                    var embedBuilder = new EmbedBuilder();

                    embedBuilder = embedBuilder
                            .WithDescription($"**Content**\n```{FormatMessageContent(notification.Message.HasValue ? notification.Message.Value.Content : null)}```");

                    if (notification.Message.HasValue)
                    {
                        embedBuilder = embedBuilder
                            .WithUserAsAuthor(notification.Message.Value.Author, notification.Message.Value.Author.Id.ToString());

                        if (notification.Message.Value.Attachments.Any())
                            embedBuilder = embedBuilder
                                .AddField(field => field
                                    .WithName("Attachments")
                                    .WithValue(string.Join(", ", notification.Message.Value.Attachments.Select(attachment => $"{attachment.Filename} ({attachment.Size}b)"))));
                    }

                    var embed = embedBuilder
                        .WithCurrentTimestamp()
                        .Build();

                    return (content, embed);
                },
                cancellationToken);

            MessageLoggingLogMessages.MessageDeletedHandled(_logger);
        }

        public async Task HandleNotificationAsync(
            MessageUpdatedNotification notification,
            CancellationToken cancellationToken)
        {
            var guild = (notification.Channel as ISocketGuildChannel)?.Guild;

            using var logScope = MessageLoggingLogMessages.BeginMessageNotificationScope(_logger, guild?.Id, notification.Channel.Id, notification.NewMessage.Id);

            MessageLoggingLogMessages.MessageUpdatedHandling(_logger);

            await TryLogAsync(
                guild,
                notification.OldMessage.HasValue ? notification.OldMessage.Value : null,
                notification.NewMessage,
                notification.Channel,
                () =>
                {
                    var content = $":pencil:Message Edited in {MentionUtils.MentionChannel(notification.Channel.Id)} `{notification.Channel.Id}`";

                    var descriptionBuilder = new StringBuilder(4096);
                    descriptionBuilder.Append($"**[Original]({notification.NewMessage.GetJumpUrl()})**\n");
                    descriptionBuilder.Append($"```{FormatMessageContent(notification.OldMessage.HasValue ? notification.OldMessage.Value.Content : null)}```\n");
                    descriptionBuilder.Append("**Updated**\n");
                    descriptionBuilder.Append($"```{FormatMessageContent(notification.NewMessage.Content)}```");

                    var embed = new EmbedBuilder()
                        .WithUserAsAuthor(notification.NewMessage.Author, notification.NewMessage.Author.Id.ToString())
                        .WithDescription(descriptionBuilder.ToString())
                        .WithCurrentTimestamp()
                        .Build();

                    return (content, embed);
                },
                cancellationToken);

            MessageLoggingLogMessages.MessageUpdatedHandled(_logger);
        }

        private string FormatMessageContent(
                string? messageContent)
            => string.IsNullOrWhiteSpace(messageContent)
                ? "[N/A]"
                // Escape backticks to preserve formatting (zero-width spaces are quite useful)
                : messageContent.Replace("```", '\u200B' + "`" + '\u200B' + "`" + '\u200B' + "`" + '\u200B');

        private async Task TryLogAsync(
            ISocketGuild? guild,
            IMessage? oldMessage,
            IMessage? newMessage,
            IISocketMessageChannel channel,
            Func<(string content, Embed embed)> renderLogMessage,
            CancellationToken cancellationToken)
        {
            if (guild is null)
            {
                MessageLoggingLogMessages.IgnoringNonGuildMessage(_logger);
                return;
            }

            // I.E. we have content for both messages and can see for sure it hasn't changed, E.G. Embed changes
            if ((oldMessage?.Content == newMessage?.Content)
                && (oldMessage is { })
                && (newMessage is { }))
            {
                MessageLoggingLogMessages.IgnoringUnchangedMessage(_logger);
                return;
            }

            var selfUser = await _selfUserProvider.GetSelfUserAsync(cancellationToken);
            MessageLoggingLogMessages.SelfUserFetched(_logger, selfUser.Id);

            if ((oldMessage?.Author ?? newMessage?.Author)?.Id == selfUser.Id)
            {
                MessageLoggingLogMessages.IgnoringSelfAuthoredMessage(_logger);
                return;
            }

            var channelIsUnmoderated = await _designatedChannelService.ChannelHasDesignationAsync(
                guild,
                channel,
                DesignatedChannelType.Unmoderated);
            if (channelIsUnmoderated)
            {
                MessageLoggingLogMessages.IgnoringUnmoderatedChannel(_logger);
                return;
            }
            MessageLoggingLogMessages.ModeratedChannelIdentified(_logger);

            var messageLogChannels = await _designatedChannelService.GetDesignatedChannelsAsync(
                guild,
                DesignatedChannelType.MessageLog);
            if (messageLogChannels.Count == 0)
            {
                MessageLoggingLogMessages.MessageLogChannelsNotFound(_logger);
                return;
            }
            MessageLoggingLogMessages.MessageLogChannelsFetched(_logger, messageLogChannels.Count);

            var (content, embed) = renderLogMessage.Invoke();

            foreach (var messageLogChannel in messageLogChannels)
            {
                MessageLoggingLogMessages.MessageLogMessageSending(_logger, messageLogChannel.Id);
                var logMessage = await messageLogChannel.SendMessageAsync(content, false, embed);
                MessageLoggingLogMessages.MessageLogMessageSent(_logger, messageLogChannel.Id, logMessage.Id);
            }
        }

        private readonly IDesignatedChannelService _designatedChannelService;
        private readonly ILogger _logger;
        private readonly ISelfUserProvider _selfUserProvider;
    }
}