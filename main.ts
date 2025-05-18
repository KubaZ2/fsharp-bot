import { Client, GatewayIntentBits, Events, ForumChannel, EmbedBuilder, REST, Routes, SlashCommandBuilder, MessageFlags } from "discord.js";
// @ts-types="@types/he"
import he from "he";
// @ts-types="@types/turndown"
import Turndown from "turndown";
import process from "node:process";
import { fetchDispatcher } from "./util.ts";
import { Buffer } from "node:buffer";

Turndown.prototype.escape = x=>x;
const turndown = new Turndown({
	codeBlockStyle: "fenced", headingStyle: "atx",
	preformattedCode: true
});

const constants = {
	...process.env as {
		[K in [
			"FORUM_CHANNEL", "SPAM_ROLE_ID", "GUILD_ID",
			"FORUM_REDDIT_TAG", "FORUM_DISCOURSE_TAG",
			"DISCORD_TOKEN", "REDDIT_USER",
			"REDDIT_PASSWORD", "REDDIT_CLIENT_ID",
			"REDDIT_CLIENT_SECRET",
			"ROLE1", "ROLE2", "ROLE3"
		][number]]: string
	},
	USER_AGENT: "FSharp Discord Bot",
	POLL_INTERVAL: 5*60*1000,
	USER_COLORS: [
		"#b366ff", "#ff6666", "#66b3ff", "#ffcc66",
		"#66ffb3", "#ff66b3", "#b3ff66", "#66ffcc",
		"#cc66ff", "#66b3cc"
	],
	REDDIT: "https://www.reddit.com/r/fsharp",
	REDDIT_OAUTH: "https://oauth.reddit.com/r/fsharp",
	DISCOURSE: "https://forums.fsharp.org"
} as const;

const	activityRoles = [
	{role: constants.ROLE1, threshold: 200},
	{role: constants.ROLE2, threshold: 1000},
	{role: constants.ROLE3, threshold: 3500}
];

const badHash = (x: string)=>{
	const mod=13;
	let d=7, p=1;
	for (let i=0; i<x.length; i++) d=(x.charCodeAt(i)*(p*=2)+d)%mod;
	return d;
};

const client = new Client({
	intents: [
		GatewayIntentBits.Guilds,
		GatewayIntentBits.GuildMessages,
		GatewayIntentBits.GuildMembers,
		GatewayIntentBits.MessageContent
	]
});

client.login(constants.DISCORD_TOKEN);

await new Promise<void>((res,rej)=>{
	client.once("ready", ()=>res());
	client.once("error", (err)=>rej(err));
});

console.log("ready");

const CLIENT_ID = client.user?.id;
if (!CLIENT_ID) throw new Error("no client id");

const kv = await Deno.openKv("./deno_kv");

function handle(f: (dispose: (()=>void)[])=>Promise<void>, err?: (msg: string)=>void) {
	const dispose: (()=>void)[] = [];

	f(dispose).catch(e=>{
		console.error(e);
		(err ?? console.error)(
			e instanceof Error ? `Error: ${e.message}` : "An unknown error occurred"
		);
	}).finally(()=>{
		for (const x of dispose) x();
	});
}

const rest = new REST().setToken(client.token!);
await rest.put(Routes.applicationCommands(client.application!.id), {
	body: [
		new SlashCommandBuilder()
			.setName("count")
			.setDescription("How many messages until you get your next role?")
			.addUserOption(x=>
				x.setName("user")
					.setDescription("User to count messages of")
					.setRequired(false)
			)
	].map(x=>x.toJSON())
});

const roleManager = (await client.guilds.fetch(constants.GUILD_ID)).roles;
const roleData = await Promise.all(
	activityRoles.map(async v=>({...v, ...await roleManager.fetch(v.role)}))
);

client.on(Events.InteractionCreate, (int) => {
	if (!int.isChatInputCommand()) return;
	const re = async (x: string, eph: boolean=true) =>
		(await int.reply({
			content: x,
			flags: eph ? MessageFlags.Ephemeral : undefined,
			withResponse: true
		})).resource?.message;

	handle(async () => {
		if (int.commandName=="count") {
			const id = int.options.getUser("user", false)?.id ?? int.user.id;
			const count = Number((await kv.get<bigint>(["messageCount", id])).value ?? 0);
			const sub = (a:string,b:string)=>int.user.id==id ? a : `<@${id}> ${b}`;

			let extra = `${sub("you've", "has")} reached the top of the ladder buddy.`;
			for (const act of roleData) {
				if (act.threshold>count) {
					extra = `just **${act.threshold-count}** more to go before ${sub("you reach", "reaches")} **${act.name}**!`
					break;
				}
			}

			await re(`${sub("you're", "is")} at **${count}** messages now! ${extra}`, false);
		}
	}, (err)=>{
		console.error("error handling interaction", err);
		void re("a great folly hath fallen upon me. sory...");
	});
});

client.on(Events.GuildMemberUpdate, (_old,newMem)=>handle(async ()=>{
	if (newMem.user.bot) return;
	const newRoles = newMem as unknown as {_roles: string[]};
	if (newRoles._roles.includes(constants.SPAM_ROLE_ID)) {
		await newMem.kick("spam bot role");
	}
}));

client.on(Events.MessageCreate, msg=>handle(async ()=>{
	if (!msg.inGuild()) return;
	const member = msg.guild.members.resolve(msg.author);
	if (!member || msg.author.bot) return;

	await kv.atomic().sum(["messageCount", member.id], BigInt(1)).commit();

	const count = (await kv.get(["messageCount", member.id])).value as bigint;
	for (const act of activityRoles) {
		if (count > act.threshold && member.roles.resolve(act.role)==null) {
			console.log(`giving role ${act.role} to ${member.id} with ${count} messages`);
			await member.roles.add(act.role);
		}
	}
}));

type PollData = {
	redditId: string|null,
	redditCommentId: string|null,
	discourseId: number|null
};

type Update = {
	type: "reddit"|"discourse",
	id: string, // unique id to deduplicate messages, in case pagination breaks (?)
	topicId: string, // topic/post id to append to or create
	topicTitle: string, // topic/post title
	url: string,

	time: Date,

	author: string,
	authorUrl: string,
	authorImage: string|null,

	// content
	html: string|null,
	text: string|null,
	link: string|null,
	image: string|null,
};

type DBTopic = {
	threadId: string, // thread channel id
	updates: string[] // array of posted update ids to message
};

let redditAuth: {token: string, expire: number}|null = null;

const f = async <T,>({url, query, reddit, header, method}: {
	url: string|URL,
	query?: Record<string,string|null|undefined>,
	method?: string,
	header?: Record<string,string|null|undefined>,
	reddit?: boolean
})=>{
	const u = new URL(url);
	let redditToken: string|null = null;
	if (reddit) redditToken=await refresh();

	if (query) for (const [k,v] of Object.entries(query)) {
		if (v!=null) u.searchParams.append(k,v);
	}

	console.debug(`fetching ${u.href}`);

	for (let didRefresh=0; didRefresh<=1; didRefresh++) {
		const ret = await fetchDispatcher<T|"reauth">({
			transform: r=>r.json() as Promise<T>,
			handleErr: r=>Promise.resolve(reddit && r.status==401 ? "reauth" as const : null)
		}, u, {
			method: method ?? "GET",
			credentials: "include",
			headers: {
				"user-agent": constants.USER_AGENT,
				"accept": "application/json",
				...redditToken!=null ? {"authorization": `bearer ${redditToken}`} : {},
				...header
			}
		});
		
		if (ret=="reauth") {
			redditToken=await refresh(true);
			continue;
		}

		return ret;
	}
	
	throw new Error("unauthorized");
};
	
const refresh = async (force?: boolean): Promise<string> => {
	const now = Date.now();
	if (!force && redditAuth!=null && now<redditAuth.expire)
		return redditAuth.token;

	const resp = await f<{access_token: string, expires_in: number}>({
		url: new URL("/api/v1/access_token", constants.REDDIT),
		method: "POST",
		query: {
			grant_type: "password",
			username: constants.REDDIT_USER,
			password: constants.REDDIT_PASSWORD
		},
		header: {
			"authorization": `basic ${Buffer.from(
				`${constants.REDDIT_CLIENT_ID}:${constants.REDDIT_CLIENT_SECRET}`
			).toString('base64')}`
		}
	});
	
	if (!("access_token" in resp)) throw new Error("access token not provided");

	console.log("authenticated to reddit");

	redditAuth = {
		token: resp.access_token,
		expire: now+resp.expires_in*1000
	};
	
	return resp.access_token;
};

let updating = false;
while (true) {
	handle(()=>(async ()=>{
		if (updating) return;

		console.debug("updating...");
		updating=true;

		const data = (await kv.get<PollData>(["poll"])).value ?? {
			redditId: null, redditCommentId: null, discourseId: null
		};

		const getRedditAvatar = async (u: string) => {
			if (u=="[deleted]") return null;

			const res = (await kv.get<string>(["redditAvatar", u])).value;
			if (res!=null) return res;

			const img = he.unescape((await f<{ data: {icon_img: string} }>({
				url: new URL(`/u/${u}/about.json`, constants.REDDIT_OAUTH),
				reddit: true
			})).data.icon_img);

			await kv.set(["redditAvatar", u], img);
			return img;
		}

		const redditResp = await f<{
			data: { children: { data: {
				selftext: string, //may be empty
				selftext_html: string|null,
				url: string,
				url_overridden_by_dest?: string,
				permalink: string,
				author: string,
				thumbnail: string, // self if not url?
				id: string,
				created_utc: number,
				title: string
			} }[] }
		}>({
			url: `${constants.REDDIT_OAUTH}/new.json`,
			reddit: true,
			query: { before: data?.redditId ? `t3_${data.redditId}` : null }
		});
			
		const redditCommentResp = await f<{
			data: { children: { data: {
				id: string,
				link_title: string,
				link_id: string,
				body: string,
				author: string,
				permalink: string,
				created_utc: number
			} }[] }
		}>({
			url: `${constants.REDDIT_OAUTH}/comments.json`,
			reddit: true,
			query: { before: data?.redditCommentId ? `t1_${data.redditCommentId}` : null }
		});
		
		const discourseResp = (await f<{
			latest_posts: {
				id: number,
				topic_id: number,
				topic_title: string,
				created_at: string,
				raw: string,
				excerpt: string,
				post_url: string,
				name: string,
				username: string,
				avatar_template: string
			}[]
		}>({
			url: `${constants.DISCOURSE}/posts.json`
		}));

		if (data.discourseId!=null) {
			discourseResp.latest_posts=discourseResp.latest_posts.filter(x=>x.id>data.discourseId!);
		}
		
		const makeReddit = async (c: (
			typeof redditCommentResp.data.children[number]["data"]
			| typeof redditResp.data.children[number]["data"]
		)) => ({
			type: "reddit",
			url: new URL(c.permalink, constants.REDDIT).href,
			authorUrl: new URL(`/u/${c.author}`, constants.REDDIT).href,
			time: new Date(c.created_utc*1000),
			author: c.author, authorImage: await getRedditAvatar(c.author)
		}) as const;

		const updates: Update[] = (await Promise.all([
			...redditResp.data.children.map(c=>c.data).map(async c=>({
				...await makeReddit(c),
				topicId: `t3_${c.id}`, id: `t3_${c.id}`,
				topicTitle: he.unescape(c.title),
				text: c.selftext=="" ? null : he.unescape(c.selftext),
				html: c.selftext_html==null ? null : he.unescape(c.selftext_html),
				link: c.url_overridden_by_dest ?? null,
				image: c.thumbnail=="self" ? null : c.thumbnail
			} satisfies Update)),

			...redditCommentResp.data.children.map(c=>c.data).map(async c=>({
				...await makeReddit(c),
				topicId: c.link_id, id: `t1_${c.id}`,
				topicTitle: he.unescape(c.link_title),
				time: new Date(c.created_utc*1000),
				text: c.body, html: c.body, link: null, image: null
			} satisfies Update)),

			...discourseResp.latest_posts.map(p=>Promise.resolve({
				type: "discourse", id: p.id.toString(),
				topicId: p.topic_id.toString(), topicTitle: p.topic_title,
				url: new URL(p.post_url, constants.DISCOURSE).href,
				time: new Date(p.created_at), author: p.name=="" ? p.username : p.name,
				authorUrl: new URL(`/u/${p.username}`, constants.DISCOURSE).href,
				authorImage: new URL(p.avatar_template.replace("{size}", "128"), constants.DISCOURSE).href,
				text: p.excerpt, html: p.raw,
				link: null, image: null
			} satisfies Update))
		])).sort(
			(a,b)=>a.time.getTime()-b.time.getTime()
		);
		
		const chan = client.channels.resolve(constants.FORUM_CHANNEL);				
		if (!(chan instanceof ForumChannel)) throw new Error("not a forum channel");

		for (const up of updates) try {
			const topicKey = ["topic", up.type, up.topicId];
			const entry = (await kv.get<DBTopic>(topicKey)).value;
			
			const abbr = (s: string, len: number) =>
				s.length > len ? `${s.substring(0, len-3)}...` : s;
			const step = 4096;

			let txt2 = up.text ?? "";
			try {
				if (up.html!=null) {
					//workaround to prevent whitespace collapse
					txt2=turndown.turndown(`<pre>\n${up.html}</pre>`);
				}
			} catch (e) {
				console.warn(`failed to convert HTML to MD: ${up.url}`, e);
			}

			const txt = abbr(
				`**(from [${up.type=="reddit" ? "Reddit" : "Discourse"}](${up.url
					}))**\n\n${txt2}`.trim(),
				4096*5
			);

			const embeds: EmbedBuilder[] = [];
			for (let i=0; i<txt.length; i+=step-3) {
				const end = i+step>=txt.length;
				embeds.push(new EmbedBuilder()
					.setColor(constants.USER_COLORS[badHash(up.authorUrl)%constants.USER_COLORS.length])
					.setTitle(abbr(`${i>0 ? "(Cont.) " : ""}${up.topicTitle}`, 256))
					.setAuthor({
						name: up.author, url: up.authorUrl,
						...up.authorImage==undefined ? {} : { iconURL: up.authorImage }
					})
					.setTimestamp(up.time)
					.setImage(up.image)
					.setURL(up.link)
					.setDescription(end ? txt.slice(i) : `${txt.slice(i,i+step-3)}...`));
			}

			if (entry==null) {
				const thread = await chan.threads.create({
					name: up.topicTitle,
					message: { embeds: [embeds[0]] },
					appliedTags: [up.type=="discourse" ? constants.FORUM_DISCOURSE_TAG : constants.FORUM_REDDIT_TAG]
				});
				
				for (const e of embeds.slice(1)) await thread.send({ embeds: [e] });
				
				await kv.set(topicKey, {
					threadId: thread.id, updates: [up.id]
				} satisfies DBTopic);

			} else if (!entry.updates.includes(up.id)) {
				const thread = chan.threads.resolve(entry.threadId);

				if (thread==null) {
					console.warn(`thread ${entry.threadId} for ${up.url} no longer exists`);
				} else {
					for (const e of embeds) await thread.send({ embeds: [e] });

					await kv.set(topicKey, {
						...entry, updates: [...entry.updates, up.id]
					} satisfies DBTopic);
				}

			} else {
				console.warn(`update ${up.url} already exists in thread ${entry.threadId}`);
			}
		} catch (err) {
			console.error(`failed to send update ${up.url}`, up, err);
		}

		const newData: PollData = {
			redditId: redditResp.data.children[0]?.data?.id ?? data?.redditId,
			redditCommentId: redditCommentResp.data.children[0]?.data?.id ?? data?.redditCommentId,
			discourseId: discourseResp.latest_posts[0]?.id ?? data?.discourseId
		};
		
		await kv.set(["poll"], newData);
		console.debug("done update");

	})().finally(()=>{
		updating=false;
	}));

	await new Promise(res=>setTimeout(res, constants.POLL_INTERVAL));
}