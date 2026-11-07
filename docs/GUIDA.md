# Guida a Planwharf

Planwharf fa lavorare una squadra di intelligenze artificiali su un tuo progetto. **Tu sei il capitano:** decidi l'obiettivo e la squadra, la squadra costruisce e controlla, e tu puoi fermare tutto in qualsiasi momento.

I pulsanti dell'app sono in inglese: in questa guida li trovi **in grassetto**, con la spiegazione in italiano.

## Cosa ti serve

- [Node.js](https://nodejs.org/) installato.
- Almeno uno strumento AI per programmare, installato sul computer e con l'accesso già fatto: Codex (ChatGPT), Claude Code, Grok CLI o Antigravity (Gemini). Planwharf usa quell'accesso e non ti chiede mai password.

## 1. Aprire Planwharf

Su Windows fai doppio clic su `Planwharf.exe`; su macOS o Linux esegui `sh ./planwharf.sh`.

Si apre una finestra con la chat al centro. Per qualche secondo in basso compare **Checking your AI tools…**: Planwharf sta controllando quali modelli puoi usare. Nel frattempo puoi già guardare i progetti e la squadra.

## 2. Lo schermo in breve

- **A sinistra:** i tuoi progetti, ognuno con il suo stato sotto il nome (pronto, in lavorazione, finito…), e il pulsante **New project**.
- **Al centro:** la conversazione con il *lead*, il modello che coordina. Qui compaiono anche le schede del lavoro: la proposta, la squadra al lavoro, il risultato e gli avvisi.
- **In basso:** un solo pulsante principale, che cambia a seconda del momento, e la casella per scrivere.
- **In alto a destra:** cinque pannelli che si aprono accanto alla chat. Quando lo spazio è poco mostrano solo l'icona.
  - **Team:** la squadra;
  - **Progress:** l'avanzamento;
  - **Usage:** i consumi;
  - **Files:** i file cambiati;
  - **Settings:** le impostazioni.

## 3. Scegliere la squadra

Apri **Team**.

1. **Lead:** scegli il modello che parla con te e organizza il lavoro.
2. **Build** (costruttore, blu): premilo su almeno un modello. Scrive il codice e i test.
3. **Verify** (controllore, arancione): premilo su un altro modello, meglio se di un'altra azienda. Ricontrolla il lavoro e corregge quello che non va. Senza controllore il lavoro parte lo stesso, ma lo controllano solo i test.
4. **Review** (verde acqua) e **Watch** (rosa) sono facoltativi: il primo legge il lavoro e segnala problemi senza toccare i file, il secondo sorveglia il lavoro in corso.

I modelli senza ruolo stanno in **panchina** (*Bench*). Per toglierne uno dalla squadra premi **Bench**, oppure scrivi in chat `metti <nome> in panchina`.

In **AI tools** accendi o spegni ogni strumento. Se uno strumento su cui hai fatto l'accesso non compare, premi **Look for models again**.

Per un modello che usi tramite API (per esempio DeepSeek o Kimi) apri **Connect a model by API**. Ti servono:
- il nome;
- l'indirizzo dell'API;
- il tipo;
- il nome della variabile d'ambiente che contiene la chiave;
- i nomi esatti dei modelli.

La chiave resta nel tuo computer: Planwharf ne salva solo il nome della variabile.

## 4. Creare un progetto

Premi **New project** e compila:

- **Name**: il nome, per esempio `Sito ricette`;
- **What should the crew build?**: cosa vuoi, a parole tue, e come si controlla che funzioni. Esempio: *"Una pagina HTML con un contatore, un pulsante per aumentare e uno per azzerare. Nessuna dipendenza. Prova entrambi i pulsanti."*;
- **Folder on your computer**: la cartella del progetto. Se la lasci vuota, Planwharf ne crea una nella cartella predefinita, che si imposta in **Settings**.

Per la prima prova usa una cartella nuova, non un progetto importante.

## 5. Far partire il lavoro

Hai tre strade:

- **Let the crew do it** (lascia fare alla squadra): la squadra pianifica, costruisce, controlla e si ferma quando l'obiettivo è raggiunto o quando arriva a un limite. Se il pulsante è grigio, la riga accanto ti dice cosa manca, e la scheda in chat ha un pulsante che apre il pannello giusto.
- **Ask me questions first** (fammi prima delle domande): il lead ti fa una domanda alla volta, ognuna con una risposta suggerita, per chiarire l'obiettivo. Rispondi in chat.
- **Approvare tu ogni passo:** descrivi il lavoro in chat. Il lead risponde con una proposta, cioè i passi e chi fa cosa, e tu premi **Approve and start** (approva e parti), oppure gli dici cosa cambiare.

## 6. Seguire il lavoro

La scheda **Crew at work** (squadra al lavoro) mostra:

- la barra di avanzamento;
- ogni passo, con il colore del ruolo, il modello e lo stato (*waiting* in attesa, *working* al lavoro, *done* fatto);
- sotto **What … is doing**, cosa sta facendo il modello in quel momento, a parole semplici.

Intanto puoi continuare a scrivere al lead. Per fermare tutto premi **Stop work** o **Stop the crew**: il lavoro fatto fin lì resta salvato, e **Resume** riprende dal punto in cui si era fermato.

Se Planwharf nota qualcosa di strano, per esempio nessun progresso da dieci minuti o un passo fallito, compare la scheda **Something needs a look**. È un invito a dare un'occhiata, non la prova che qualcosa si è rotto. Con **Ignore** la nascondi.

## 7. Il risultato

Alla fine compare la scheda **Work finished** con:

- i passi completati;
- il tempo impiegato;
- quanti file sono cambiati e non ancora salvati in Git.

Premi **See changed files** per vedere quali sono, poi apri la cartella del progetto e prova il risultato.

Planwharf **non fa mai da solo** commit, push o pubblicazioni. Se il risultato non va, scrivilo in chat: il lead prepara una correzione.

## 8. Consumi

**Usage** mostra:

- quanta quota ti resta per ogni strumento;
- le chiamate, il tempo e i token di ogni modello.

Se uno strumento non comunica un dato, vedi "not reported": Planwharf non stima e non inventa mai i numeri.

## 9. Impostazioni utili

In **Settings** trovi:

- **This project:** la cartella del progetto;
- **New projects:** la cartella in cui creare i progetti nuovi;
- **Approvals:** se l'interruttore è acceso, il lead può far partire da solo il lavoro già approvato;
- **Look:** lo sfondo, scuro (predefinito) o chiaro. Con lo sfondo scuro scegli anche il colore del testo: bianco, crema, menta, cielo, ambra o lavanda;
- **Move a project:** salva un progetto in un file, o aprine uno salvato su un altro computer. Il codice resta nella cartella del progetto;
- **Updates:** gli aggiornamenti dell'app;
- **Remove project** e **Quit:** chiedono una seconda conferma. Rimuovere un progetto non cancella la sua cartella. Chiudendo, il lavoro non finito viene salvato e si riprende la volta dopo.

## 10. Comandi veloci in chat

Questi comandi li esegue Planwharf da solo, senza chiamare nessun modello:

| Scrivi | Cosa fa |
| --- | --- |
| `squadra`, `team`, `panchina` | apre Team |
| `stato`, `avanzamento`, `progress` | apre Progress |
| `consumi`, `usage` | apre Usage |
| `file cambiati`, `files` | apre Files |
| `impostazioni`, `settings` | apre Settings |
| `metti <modello> in panchina` | toglie tutti i ruoli a quel modello, dopo una conferma |

Tutto il resto va al lead.

## Se qualcosa non va

| Cosa vedi | Cosa fare |
| --- | --- |
| **Let the crew do it** è grigio | Leggi la riga accanto: dice cosa manca. |
| Resta **Checking your AI tools…** | Aspetta qualche secondo: Planwharf sta interrogando gli strumenti AI. |
| Manca un modello in Team | Fai l'accesso nello strumento, controlla che sia **On** e premi **Look for models again**. |
| Un passo è fallito | Leggi il passo e l'avviso, poi di' al lead cosa cambiare oppure premi **Resume**. |
| **Resume** chiede conferma | La cartella è cambiata da quando il lavoro si è fermato: controlla e conferma. |
| La quota mostra un motivo invece di un numero | Quello strumento non comunica la quota. |

Quando chiedi aiuto, indica l'errore che vedi e il nome esatto del modello. Non condividere mai i dati di accesso.
