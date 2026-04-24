import { copyFile, mkdir, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { defineConfig } from "vitepress";

const docsRoot = "https://docs.boltz.exchange";
const siteUrl = "https://btcpay.docs.boltz.exchange";

const sidebarItems = [
  { text: "👋 Introduction", link: "/index" },
  { text: "🚧 Limitations", link: "/limitations" },
  { text: "🏗️ Building the Plugin", link: "/building-the-plugin" },
  { text: "🧪 Regtest Setup", link: "/regtest-setup" },
  { text: "🏠 Docs Home", link: docsRoot, target: "_self" },
];

// https://vitepress.dev/reference/site-config
export default defineConfig({
  title: "Boltz BTCPay Plugin",
  description: "Boltz BTCPay Plugin Docs",
  head: [["link", { rel: "icon", href: "/assets/logo.svg" }]],
  themeConfig: {
    logo: "/assets/logo.svg",
    search: {
      provider: "local",
      options: {
        detailedView: true,
      },
    },
    nav: [{ text: "🏠 Docs Home", link: docsRoot, target: "_self" }],
    sidebar: [
      {
        items: sidebarItems,
      },
    ],
    socialLinks: [
      {
        icon: "github",
        link: "https://github.com/BoltzExchange/boltz-btcpay-plugin",
      },
    ],
  },
  // Ignore dead links to localhost
  ignoreDeadLinks: [/https?:\/\/localhost/],

  // Copy raw .md sources into dist so each page is also reachable as
  // /<page>.md, and emit llms.txt for LLM crawlers.
  async buildEnd(siteConfig) {
    await Promise.all(
      siteConfig.pages.map(async (page) => {
        const src = join(siteConfig.srcDir, page);
        const dest = join(siteConfig.outDir, page);
        await mkdir(dirname(dest), { recursive: true });
        await copyFile(src, dest);
      }),
    );

    const links = sidebarItems
      .filter((item) => item.link.startsWith("/"))
      .map((item) => `- [${item.text}](${siteUrl}${item.link}.md)`)
      .join("\n");

    await writeFile(
      join(siteConfig.outDir, "llms.txt"),
      `# Boltz BTCPay Plugin\n\n> Boltz BTCPay Plugin Docs\n\n## Docs\n\n${links}\n`,
    );
  },
});
