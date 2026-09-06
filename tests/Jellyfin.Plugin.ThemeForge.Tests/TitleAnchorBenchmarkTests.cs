using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// The adversarial benchmark for title matching.
/// </summary>
/// <remarks>
/// <para>
/// The matcher this replaced passed every test it had, because its golden set used distinctive
/// multi-word titles — the case it never got wrong. The cases here are the ones it got wrong on a
/// real library: ordinary one-word show names, sibling works that share a name, and words that
/// merely start the same way.
/// </para>
/// <para>
/// Accepts and rejects are reported separately on purpose. A matcher that rejects everything
/// passes half a benchmark and is useless, so the accept set is as much of the test as the
/// reject set.
/// </para>
/// </remarks>
public class TitleAnchorBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public TitleAnchorBenchmarkTests(ITestOutputHelper output) => _output = output;

    /// <summary>Titles that must be recognised as naming the wanted work.</summary>
    public static readonly (string Wanted, string Candidate)[] MustAccept =
    {
        // Plain, the form most uploads take.
        ("Firefly", "Firefly Theme Song"),
        ("Firefly", "Firefly - Main Title"),
        ("Battlestar Galactica", "Battlestar Galactica Main Title Theme"),
        ("The Wire", "The Wire Theme Song - Way Down In The Hole"),
        ("Breaking Bad", "Breaking Bad Main Title Theme"),
        ("Game of Thrones", "Game of Thrones Main Title"),
        ("Stranger Things", "Stranger Things Theme - Kyle Dixon & Michael Stein"),
        ("Twin Peaks", "Twin Peaks Theme (Full)"),
        ("The Sopranos", "The Sopranos Theme - Woke Up This Morning"),
        ("Doctor Who", "Doctor Who Theme (1963)"),
        ("The X-Files", "The X-Files Theme Song"),
        ("Westworld", "Westworld Main Title Theme (HBO)"),
        ("Fringe", "Fringe Main Titles (Extended)"),
        ("Cowboy Bebop", "Cowboy Bebop - Tank! (Opening)"),
        ("Attack on Titan", "Attack on Titan OP 1"),
        ("The Mandalorian", "The Mandalorian | Main Theme"),
        ("Better Call Saul", "Better Call Saul Theme Song"),
        ("Peaky Blinders", "Peaky Blinders Theme - Red Right Hand"),
        ("Star Trek: The Next Generation", "Star Trek: The Next Generation Main Title"),
        ("Star Trek: Deep Space Nine", "Star Trek Deep Space Nine - Theme"),
        ("The Simpsons", "The Simpsons Theme Song HD"),
        ("Sherlock", "Sherlock BBC Theme"),
        ("Blade Runner", "Blade Runner - Main Titles (Vangelis)"),
        ("Interstellar", "Interstellar Main Theme - Hans Zimmer"),
        ("Jurassic Park", "Jurassic Park Theme - John Williams"),
        ("The Godfather", "The Godfather - Main Title (Love Theme)"),
        ("Inception", "Inception OST - Time"),
        ("The Good, the Bad and the Ugly", "The Good the Bad and the Ugly - Main Theme"),

        // Awkward but correct: the work named after the artist, or only inside brackets.
        ("Firefly", "Sonny Rhodes - The Ballad of Serenity (Firefly Opening Theme Song)"),
        ("Halt and Catch Fire", "Trentemøller - Snowflake (Halt and Catch Fire Theme)"),
        ("Succession", "Nicholas Britell - Succession (Main Title Theme)"),
        ("True Detective", "The Handsome Family - Far From Any Road [True Detective Theme]"),
        ("Dexter", "Rolfe Kent - Blood Theme (Dexter Opening)"),
        ("Lost", "LOST Main Theme (Season 1)"),
        ("Lost", "Michael Giacchino - Lost - Main Title"),
        ("Friends", "Friends Theme Song - The Rembrandts"),
        ("House", "House M.D. Theme - Teardrop"),
        ("Bones", "Bones Theme Song - The Crystal Method"),

        // The article: present in the candidate, so no penalty applies.
        ("The Office", "The Office Theme Song"),
        ("The Boys", "The Boys - Main Title Theme"),

        // The article missing from the candidate still matches, at a lower score.
        ("The Office", "Office Theme Song (US)"),
        ("The Expanse", "Expanse Main Title Theme"),

        // Spelling and formatting differences that do not change which work is named.
        ("Amélie", "Amelie - Main Theme (Yann Tiersen)"),
        ("Æon Flux", "Aeon Flux Theme"),
        ("Marvel's Agents of S.H.I.E.L.D.", "Agents of SHIELD Main Theme"),
        ("Spider-Man", "Spider Man Theme Song"),
        ("Rocky II", "Rocky 2 - Main Theme"),
    };

    /// <summary>Titles that must not be accepted as naming the wanted work.</summary>
    public static readonly (string Wanted, string Candidate)[] MustReject =
    {
        // The failures that motivated the rewrite: the wanted title as a fragment of a phrase.
        ("Lost", "Getting lost in the woods ASMR"),
        ("House", "Building a house timelapse"),
        ("House", "Deep House Mix 2024"),
        ("The Office", "office chair review"),
        ("Girls", "Spice Girls - Wannabe"),
        ("Friends", "How to make friends as an adult"),

        // Sibling works that share a word. Each is a genuine theme of a real show.
        ("Girls", "The Golden Girls Theme Song"),
        ("Girls", "Gilmore Girls Theme Song - Where You Lead"),
        ("Girls", "2 Broke Girls Theme Song"),
        ("Girls", "The Powerpuff Girls Theme Song"),
        ("Lost", "Lost in Space Theme"),
        ("Lost", "Lost Girl Theme Song"),
        ("Star Trek", "Star Trek: Deep Space Nine - Theme"),
        ("Star Trek", "Star Trek Voyager Main Title"),
        ("Doctor Who", "Doctor Who Confidential Theme"),
        ("The Office", "The Office UK vs The Office US comparison"),
        ("Fargo", "Fargo Season 2 recap"),
        ("Alien", "Aliens Main Theme"),
        ("Alien", "Alien Covenant Theme"),
        ("Titanic", "Titanic II Theme Song"),
        ("Halo", "Halo Wars Main Theme"),
        ("Rome", "Rome Total War Main Theme"),
        ("Dark", "Dark Souls Main Theme"),
        ("Castle", "Castlevania Main Theme"),
        ("Rocky", "Rocky Horror Picture Show Theme"),
        ("The Crown", "The Crown Jewels documentary"),
        ("Chernobyl", "Chernobyl Diaries Theme"),
        ("Narcos", "Narcos Mexico Theme Song"),
        ("Bad Boys", "Bad Boys for Life Theme"),
        ("Top Gun", "Top Gun Maverick Theme Song"),

        // Words that merely start the same way. These are the cases a similarity threshold
        // alone scores above 0.90 and gets wrong.
        ("Dark", "Darkside - Alan Walker"),
        ("You", "Young Justice Theme"),
        ("Bones", "Bonesaw Theme"),
        ("Chuck", "Chuckle Brothers Theme"),

        // Not the work at all.
        ("Firefly", "Owl City - Fireflies"),
        ("The Wire", "Wireless charging explained"),
        ("Fringe", "Fringe Festival 2019 Highlights"),
    };

    /// <summary>
    /// Titles that anchor cleanly but are too ordinary to trust unattended without something
    /// else in the candidate agreeing.
    /// </summary>
    public static readonly (string Wanted, string Candidate, bool HasClaim)[] NeedsCorroboration =
    {
        ("Friends", "Marshmello - FRIENDS", false),
        ("Bones", "Bones - Imagine Dragons", false),
        ("Friends", "Friends Theme Song - The Rembrandts", true),
        ("Lost", "Lost", false),
        ("Lost", "Lost - Main Title", true),
        ("House", "House", false),
        ("Girls", "Girls - Opening Theme", true),
    };

    [Fact]
    public void EveryCorrectThemeIsAccepted()
    {
        var failures = MustAccept
            .Select(pair => (pair, result: TitleAnchor.Match(pair.Wanted, pair.Candidate)))
            .Where(row => row.result.Rejected)
            .Select(row => $"  {row.pair.Wanted,-34} <- {row.pair.Candidate}  ({row.result.Reason})")
            .ToList();

        Report("false rejects", failures, MustAccept.Length);
        Assert.Empty(failures);
    }

    [Fact]
    public void EveryWrongCandidateIsRejected()
    {
        var failures = MustReject
            .Select(pair => (pair, result: TitleAnchor.Match(pair.Wanted, pair.Candidate)))
            .Where(row => !row.result.Rejected)
            .Select(row => $"  {row.pair.Wanted,-34} <- {row.pair.Candidate}  (scored {row.result.Score:0.00}: {row.result.Reason})")
            .ToList();

        Report("false accepts", failures, MustReject.Length);
        Assert.Empty(failures);
    }

    [Fact]
    public void AnOrdinaryTitleIsOnlyTrustedWhenSomethingElseAgrees()
    {
        foreach (var (wanted, candidate, hasClaim) in NeedsCorroboration)
        {
            var result = TitleAnchor.Match(wanted, candidate);

            Assert.False(result.Rejected, $"{wanted} <- {candidate}: {result.Reason}");
            Assert.True(result.RequiresCorroboration, $"{wanted} <- {candidate} should need corroboration");
            Assert.Equal(hasClaim, TitleAnchor.ClaimsToBeATheme(candidate));
        }
    }

    [Fact]
    public void ADistinctiveTitleStandsOnItsOwn()
    {
        foreach (var wanted in new[] { "Battlestar Galactica", "Peaky Blinders", "Cowboy Bebop", "Chernobyl" })
        {
            var result = TitleAnchor.Match(wanted, wanted + " Main Theme");
            Assert.False(result.RequiresCorroboration, $"{wanted} should not need corroboration");
        }
    }

    private void Report(string label, IReadOnlyList<string> failures, int total)
    {
        _output.WriteLine($"{total - failures.Count}/{total} correct — {failures.Count} {label}");
        foreach (var failure in failures)
        {
            _output.WriteLine(failure);
        }
    }
}
