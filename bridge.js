
  RUST EMBLEM BRIDGE (WEBSOCKET VERSION)
  Best for servers using 'rcon.web 1' (Standard for Rust)
 

const express = require('express');
const bodyParser = require('body-parser');
const fs = require('fs');
const WebSocket = require('ws');  Standard WebSocket library
const cors = require('cors');
const path = require('path');

const app = express();
const PORT = 3000;

 ==========================================
 1. CONFIGURATION
 ==========================================
const VPS_PUBLIC_IP = 159.89.137.231; 

 Your Rust Server Details
const RUST_SERVER_IP = 172.240.87.77;  
const RUST_RCON_PORT = 23015;            
const RUST_RCON_PASS = YOUR_RCON_PASSWORD_HERE;  --- PUT PASSWORD HERE
 ==========================================

app.use(cors());
app.use(bodyParser.json({ limit '50mb' }));
app.use(express.static('public')); 

const publicDir = path.join(__dirname, 'public');
if (!fs.existsSync(publicDir)){
    fs.mkdirSync(publicDir);
}

app.post('upload', async (req, res) = {
    const { steamId, imageData } = req.body;

    if (!steamId  !imageData) {
        return res.status(400).send({ success false, message 'Missing data' });
    }

    console.log(`[+] Receiving emblem for SteamID ${steamId}`);

    const base64Data = imageData.replace(^dataimagepng;base64,, );
    const fileName = `${steamId}.png`;
    const filePath = path.join(publicDir, fileName);

    fs.writeFile(filePath, base64Data, 'base64', async (err) = {
        if (err) {
            console.error([-] File save error, err);
            return res.status(500).send({ success false, message 'Server file error' });
        }

        console.log(`[+] Image saved ${fileName}`);
        const downloadUrl = `http${VPS_PUBLIC_IP}${PORT}${fileName}`;

         Send RCON Command via WebSocket
        try {
            await sendWebRconCommand(steamId, downloadUrl);
            res.send({ success true, message 'Image processed and RCON sent.' });
        } catch (rconError) {
            console.error([-] RCON Failed, rconError.message  rconError);
            res.send({ success true, message 'Image saved, but RCON failed. Check logs.' });
        }
    });
});


  WEBSOCKET RCON FUNCTION
 
function sendWebRconCommand(steamId, url) {
    return new Promise((resolve, reject) = {
        console.log(`[] Connecting to WebRcon (ws${RUST_SERVER_IP}${RUST_RCON_PORT})...`);

         Rust WebRcon URL format wsIPPORTPASSWORD
        const wsAddress = `ws${RUST_SERVER_IP}${RUST_RCON_PORT}${RUST_RCON_PASS}`;
        const ws = new WebSocket(wsAddress);

        ws.on('open', function open() {
            console.log('[] Connected to WebRcon. Sending command...');
            
             The JSON Payload Rust expects
            const payload = {
                Identifier 1,
                Message `emblem.update ${steamId} ${url}`,
                Name WebRcon
            };

            ws.send(JSON.stringify(payload));
        });

        ws.on('message', function incoming(data) {
             We received a response, which means it worked!
            const response = JSON.parse(data);
            console.log(`[] Rust Responded ${response.Message}`);
            ws.close();
            resolve();
        });

        ws.on('error', function error(err) {
            reject(err);
        });
        
         Timeout if server accepts connection but never replies
        setTimeout(() = {
            if(ws.readyState !== WebSocket.CLOSED) {
               ws.close();
                We resolve anyway because sometimes Rust executes the command but forgets to reply
               console.log('[!] Connection closed (Timeout), assuming command sent.');
               resolve(); 
            }
        }, 3000); 
    });
}

app.listen(PORT, () = {
    console.log(`BRIDGE ONLINE at http${VPS_PUBLIC_IP}${PORT}`);
});