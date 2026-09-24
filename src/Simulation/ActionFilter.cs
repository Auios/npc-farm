namespace NpcFarm.Simulation;

public static class ActionFilter {
    public const float HungerUrgent = 0.55f;
    public const float EnergyUrgent = 0.35f;
    public const float SocialUrgent = 0.45f;
    public const float TalkRange = 3f;

    public static bool IsChatAction(NpcAction action) => action is
        NpcAction.Talk or NpcAction.OfferGift or NpcAction.RequestItem
        or NpcAction.ProposeTrade or NpcAction.TakeWithoutConsent;

    public static List<ActionOption> BuildValidActions(World world, Npc npc) {
        var clock = world.Clock;
        var map = world.Map;
        var options = new List<ActionOption>();
        var conversation = world.ConversationOf(npc.Id);

        var nearby = world.NpcsNearby(npc, TalkRange)
            .Where(o => o.TalkCooldownHours <= 0 || conversation?.ParticipantIds.Contains(o.Id) == true)
            .Take(4)
            .ToList();
        bool canTalk = nearby.Count > 0 && npc.Needs.Social <= SocialUrgent && npc.TalkCooldownHours <= 0;
        bool lonelyWithCompany = npc.Needs.Social <= 0.25f && nearby.Count > 0 && npc.TalkCooldownHours <= 0;

        if (conversation is not null)
            AddConversationOptions(npc, conversation, nearby, options);

        if (lonelyWithCompany && !(clock.IsNight || npc.Needs.Energy <= EnergyUrgent)) {
            if (options.All(o => o.Action != NpcAction.Talk)) {
                options.Add(new ActionOption {
                    Action = NpcAction.Talk,
                    Id = "talk",
                    Description = $"talk now; loneliness is urgent ({npc.Needs.Social:0.00}) and friends are beside you"
                });
            }
            if (clock.IsMarketOpen && npc.Gold >= 2 && conversation is null) {
                var tavern = map.Buildings.First(b => b.Kind == BuildingKind.Tavern);
                options.Add(new ActionOption {
                    Action = NpcAction.Tavern,
                    Id = "tavern",
                    Description = "invite company to the tavern for a drink and gossip",
                    TargetTile = map.ClampWalkable(tavern.Door)
                });
            }
            if (npc.Needs.Hunger >= 0.85f) {
                options.Add(new ActionOption {
                    Action = NpcAction.Eat,
                    Id = "eat",
                    Description = $"eat quickly; hunger critical ({npc.Needs.Hunger:0.00})",
                    TargetTile = npc.Tile
                });
            }
            return options.Take(8).ToList();
        }

        bool canWork = clock.IsWorkHours && npc.Needs.Energy > 0.2f;
        if (canWork) {
            var workplace = map.WorkplaceFor(npc.Job);
            options.Add(new ActionOption {
                Action = NpcAction.Work,
                Id = "work",
                Description = $"work at {workplace.Name}; daytime job available, energy ok, earns gold",
                TargetTile = map.ClampWalkable(workplace.Door)
            });
        }

        bool marketOpen = clock.IsMarketOpen;
        if (npc.Needs.Hunger >= HungerUrgent) {
            if (npc.Gold >= 3 && marketOpen) {
                var market = map.Buildings.First(b => b.Kind == BuildingKind.Market);
                options.Add(new ActionOption {
                    Action = NpcAction.BuyFood,
                    Id = "buy_food",
                    Description = $"buy food at market; hunger high ({npc.Needs.Hunger:0.00}), has gold",
                    TargetTile = map.ClampWalkable(market.Door)
                });
            }
            options.Add(new ActionOption {
                Action = NpcAction.Eat,
                Id = "eat",
                Description = npc.Inventory.Items.Any(i => i.Tags.Contains("food") && !i.IsLivelihoodAsset)
                    ? $"eat food from your pack; hunger high ({npc.Needs.Hunger:0.00})"
                    : $"scrounge a meal; hunger high ({npc.Needs.Hunger:0.00})",
                TargetTile = npc.Tile
            });
        }

        if (npc.Needs.Energy <= EnergyUrgent || clock.IsNight) {
            var home = map.Homes[npc.HomeId];
            options.Add(new ActionOption {
                Action = NpcAction.Sleep,
                Id = "sleep",
                Description = clock.IsNight
                    ? "sleep at home; it is night and energy needs rest"
                    : $"sleep at home; energy low ({npc.Needs.Energy:0.00})",
                TargetTile = map.ClampWalkable(home.Door)
            });
        }

        if (canTalk && options.All(o => o.Id != "talk")) {
            options.Add(new ActionOption {
                Action = NpcAction.Talk,
                Id = "talk",
                Description = $"talk to a nearby person; social need up ({npc.Needs.Social:0.00}), {nearby.Count} people nearby"
            });
        }

        if (conversation is null)
            AddOpportunisticExchange(npc, nearby, options);

        if (clock.IsMarketOpen && npc.Gold >= 2 && conversation is null) {
            var tavern = map.Buildings.First(b => b.Kind == BuildingKind.Tavern);
            options.Add(new ActionOption {
                Action = NpcAction.Tavern,
                Id = "tavern",
                Description = "visit tavern; open and has gold for a drink and gossip",
                TargetTile = map.ClampWalkable(tavern.Door)
            });
        }

        var homeDoor = map.ClampWalkable(map.Homes[npc.HomeId].Door);
        options.Add(new ActionOption {
            Action = NpcAction.GoHome,
            Id = "go_home",
            Description = "go home and idle safely",
            TargetTile = homeDoor
        });

        options.Add(new ActionOption {
            Action = NpcAction.Rest,
            Id = "rest",
            Description = "rest where you are; low-risk pause",
            TargetTile = npc.Tile
        });

        if (npc.Interrupted is not null) {
            options.Insert(0, new ActionOption {
                Action = NpcAction.ResumeInterrupted,
                Id = "resume_interrupted",
                Description = $"go back to {npc.Interrupted.Action} after the interruption, unless something more urgent came up"
            });
        }

        if (!world.Player.TerminalOpen || world.Player.ActiveNpcId == npc.Id) {
            bool summoned = world.Player.SpeakRequestTargetId == npc.Id;
            bool hasReport = npc.RecentReports.Count > 0 && (clock.HourOfDay >= 16f || !clock.IsWorkHours);
            bool needsHelp = npc.Needs.Hunger > 0.7f || npc.Gold < 6 || npc.Needs.MoneyPressure(npc.Gold) >= 0.7f;
            bool wantsChat = npc.Personality.Sociability > 0.55f && npc.Needs.Social < 0.4f && clock.HourOfDay is >= 11f and <= 20f;
            if (summoned || hasReport || needsHelp || wantsChat) {
                if (summoned) npc.ContactReason = PlayerContactReason.Summoned;
                else if (hasReport) npc.ContactReason = PlayerContactReason.ReportWork;
                else if (needsHelp) npc.ContactReason = PlayerContactReason.NeedHelp;
                else npc.ContactReason = PlayerContactReason.JustTalk;

                var computer = map.Buildings.First(b => b.Kind == BuildingKind.Computer);
                string desc = npc.ContactReason switch {
                    PlayerContactReason.Summoned => "go to the town computer; the overseer asked to speak with you",
                    PlayerContactReason.ReportWork => "go to the town computer to tell the overseer what you worked on / produced",
                    PlayerContactReason.NeedHelp => "go to the town computer to ask the overseer for help with a problem",
                    _ => "go to the town computer to talk with the overseer"
                };
                options.Insert(0, new ActionOption {
                    Action = NpcAction.ContactPlayer,
                    Id = "contact_player",
                    Description = desc,
                    TargetTile = map.ClampWalkable(computer.Door)
                });
            }
        }

        if (world.Player.SpeakRequestTargetId is string speakId
            && speakId != npc.Id
            && !world.Player.SpeakRequestSeenBy.Contains(npc.Id)) {
            var computer = map.Buildings.First(b => b.Kind == BuildingKind.Computer);
            var door = map.ClampWalkable(computer.Door);
            float dx = npc.DrawX - door.X;
            float dy = npc.DrawY - door.Y;
            if (dx * dx + dy * dy <= TalkRange * TalkRange) {
                var target = world.Npcs.FirstOrDefault(n => n.Id == speakId);
                if (target is not null) {
                    options.Insert(0, new ActionOption {
                        Action = NpcAction.InformNpc,
                        Id = "inform_npc",
                        Description = $"tell {target.Name} that the overseer wants to speak with them",
                        TargetNpcId = target.Id,
                        TargetTile = target.Tile
                    });
                }
            }
        }

        return options.Take(8).ToList();
    }

    private static void AddConversationOptions(Npc npc, Conversation conversation, List<Npc> nearby, List<ActionOption> options) {
        bool wantsOut = npc.Needs.Social >= 0.65f || npc.Needs.MoneyPressure(npc.Gold) >= 0.6f;
        options.Add(new ActionOption {
            Action = NpcAction.LeaveConversation,
            Id = "leave",
            Description = wantsOut
                ? "leave the conversation; you are socially satisfied or need to earn gold"
                : "leave the conversation and get back to your own business"
        });

        var partner = nearby.FirstOrDefault(n => conversation.ParticipantIds.Contains(n.Id))
                      ?? nearby.FirstOrDefault();
        if (partner is null) return;

        options.Add(new ActionOption {
            Action = NpcAction.Talk,
            Id = "continue_talk",
            Description = $"keep talking with {partner.Name}; affinity {npc.GetAffinity(partner.Id):0.00}",
            TargetNpcId = partner.Id,
            TargetTile = partner.Tile
        });

        var gift = npc.Inventory.Giftable.OrderBy(i => i.UnitValue).FirstOrDefault();
        if (gift is not null && npc.Personality.Empathy >= 0.25f) {
            options.Add(new ActionOption {
                Action = NpcAction.OfferGift,
                Id = "offer_gift",
                Description = npc.Personality.Greed < 0.45f
                    ? $"give {gift.Name} to {partner.Name}; you are generous and not desperate for gold"
                    : $"offer {gift.Name} to {partner.Name} as a gift",
                TargetNpcId = partner.Id,
                ItemId = gift.Id,
                TargetTile = partner.Tile
            });
        }

        var requested = partner.Inventory.Giftable.OrderByDescending(i => i.UnitValue).FirstOrDefault();
        if (requested is not null) {
            options.Add(new ActionOption {
                Action = NpcAction.RequestItem,
                Id = "request_item",
                Description = $"ask {partner.Name} for {requested.Name}; depends on their kindness and your relationship",
                TargetNpcId = partner.Id,
                ItemId = requested.Id,
                TargetTile = partner.Tile
            });
            int price = TradeResolver.SuggestedPrice(partner, requested);
            options.Add(new ActionOption {
                Action = NpcAction.ProposeTrade,
                Id = "propose_trade",
                Description = $"offer about {price} gold to buy {requested.Name} from {partner.Name}; you want more gold long-term but need goods",
                TargetNpcId = partner.Id,
                ItemId = requested.Id,
                GoldAmount = price,
                TargetTile = partner.Tile
            });
        }

        var stealTarget = partner.Inventory.Stealable.OrderByDescending(i => i.UnitValue).FirstOrDefault();
        if (stealTarget is not null && (npc.Personality.Honesty < 0.55f || npc.Personality.Aggression > 0.45f)) {
            options.Add(new ActionOption {
                Action = NpcAction.TakeWithoutConsent,
                Id = "take_without_consent",
                Description = $"take {stealTarget.Name} from {partner.Name} without asking; cunning or greedy, and it will damage the relationship",
                TargetNpcId = partner.Id,
                ItemId = stealTarget.Id,
                TargetTile = partner.Tile
            });
        }
    }

    private static void AddOpportunisticExchange(Npc npc, List<Npc> nearby, List<ActionOption> options) {
        var partner = nearby.FirstOrDefault();
        if (partner is null) return;
        var gift = npc.Inventory.Giftable.FirstOrDefault();
        if (gift is null || npc.Personality.Empathy < 0.55f || npc.Personality.Greed > 0.5f) return;
        options.Add(new ActionOption {
            Action = NpcAction.OfferGift,
            Id = "offer_gift",
            Description = $"walk over and give {gift.Name} to {partner.Name}",
            TargetNpcId = partner.Id,
            ItemId = gift.Id,
            TargetTile = partner.Tile
        });
    }

    public static List<ActionOption> BuildTalkTargets(World world, Npc npc) {
        return world.NpcsNearby(npc, TalkRange)
            .Where(o => o.TalkCooldownHours <= 0)
            .Take(5)
            .Select(o => new ActionOption {
                Action = NpcAction.Talk,
                Id = o.Id,
                Description = $"talk with {o.Name} ({o.OccupationName}); affinity {npc.GetAffinity(o.Id):0.00}",
                TargetNpcId = o.Id,
                TargetTile = o.Tile
            })
            .ToList();
    }

    public static bool NeedsUrgentDecision(Npc npc, GameClock clock) {
        if (npc.DecisionPending) return false;
        if (npc.CurrentAction is null && !npc.IsMoving && npc.ActionRemainingHours <= 0)
            return true;
        if (npc.Needs.Hunger >= 0.85f && npc.CurrentAction is not (NpcAction.Eat or NpcAction.BuyFood))
            return true;
        if (npc.Needs.Energy <= 0.15f && npc.CurrentAction != NpcAction.Sleep)
            return true;
        if (clock.IsNight && npc.CurrentAction is NpcAction.Work or NpcAction.Tavern)
            return true;
        return false;
    }
}
