"""Who made the tools, in one place: the project page and the README of every pack that is built say it."""
AUTHOR = "petergowild"
DISCORD = "petergowild"                         # a user name, not a link
GITHUB = "https://github.com/peter-w07"
REPO = "https://github.com/peter-w07/los-santos-reskate"
SITE = "https://peter-w07.github.io/los-santos-reskate/"


def lines():
    """Markdown list of where to find the author."""
    out = [f"- Discord: `{DISCORD}`"]
    out.append(f"- GitHub: [{GITHUB.replace('https://github.com/', '')}]({GITHUB})")
    return out
