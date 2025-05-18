type Dispatcher = ({
	type: "proxy", proxy: Deno.HttpClient
}|{
	type: "main"
})&{errorWait?: number, name: string};

const dispatchers: Dispatcher[] = [{type: "main", name: "main"}];
const waiters: (()=>void)[] = [];

export function shuffle<T>(arr: T[]) {
	for (let i=1; i<arr.length; i++) {
		const j = Math.floor(Math.random()*(i+1));
		const x = arr[j];
		arr[j]=arr[i];
		arr[i]=x;
	}
}

export async function addProxies(proxiesPath: string) {
	dispatchers.splice(0,dispatchers.length);

	let prox = JSON.parse(await Deno.readTextFile(proxiesPath)) as string[]|{proxyFetchUrl: string};
	if ("proxyFetchUrl" in prox) {
		console.log("fetching proxies...");
		prox = (await (await fetch(prox.proxyFetchUrl)).text()).trim().split("\n");
	}

	console.log(`adding ${prox.length} proxies`);

	let pi=1;
	for (const p of prox) {
		const parts = p.split(":");
		if (parts.length!=2 && parts.length!=4)
			throw new Error(`expected 2 (host,port) or 4 parts (host,port,user,pass) for proxy ${p}`);

		const proxy = Deno.createHttpClient({
			proxy: {
				url: `http://${parts[0]}:${parts[1]}`,
				basicAuth: parts.length==2 ? undefined : {
					username: parts[2], password: parts[3]
				}
			}
		});

		dispatchers.push({type: "proxy", proxy, name: `Proxy #${pi++} (${parts[0]}:${parts[1]})`});
	}
	shuffle(dispatchers);
}

// await addProxies("proxies.json");

const dispatcherWait = 1000, dispatcherErrorWait = 30_000
const dispatcherErrorWaitMul = 2, dispatcherTimeout = 120_000;
const maxDispatcherErrorWait = 60_000*5;

export async function fetchDispatcher<T>({ transform, handleErr }: {
	transform: (r: Response) => Promise<T>,
	handleErr: (r: Response) => Promise<T|null>
}, ...args: Parameters<typeof fetch>): Promise<T> {
	let err: Error|null=null;
	for (let retryI=0; retryI<5; retryI++) {
		while (dispatchers.length==0) {
			await new Promise<void>(res=>waiters.push(res));
		}

		const d = dispatchers.pop()!;
		err=null;
		let rateLimitMinWait = 0;

		try {
			const resp = await fetch(args[0], {
				...args[1],
				client: d.type=="proxy" ? d.proxy : undefined,
				signal: AbortSignal.timeout(dispatcherTimeout)
			});

			if (resp.status!=200) {
				const d = await handleErr(resp);
				if (d!=null) return d;

				if (resp.status==429 && resp.headers.has("Retry-After")) {
					const wait = Number.parseFloat(resp.headers.get("Retry-After")!)*1000;
					if (isFinite(wait)) rateLimitMinWait=wait;
				}

				throw new Error(resp.statusText);
			}

			return await transform(resp)
		} catch (e) {
			if (e instanceof Error) err=e;
		} finally {
			if (err) {
				d.errorWait = d.errorWait==undefined ? dispatcherErrorWait
					: d.errorWait*dispatcherErrorWaitMul;
				d.errorWait = Math.min(Math.max(d.errorWait, rateLimitMinWait), maxDispatcherErrorWait);
				console.warn(`\nError with dispatcher ${d.name}, waiting ${(d.errorWait/60/1000).toFixed(2)} min.\n`);
			} else {
				delete d.errorWait;
			}

			setTimeout(() => {
				dispatchers.unshift(d);
				const w = waiters.shift();
				if (w!==undefined) w();
			}, d.errorWait ?? dispatcherWait);
		}
	}

	throw new Error(`ran out of retries during fetch:\n${err!}`);
}
