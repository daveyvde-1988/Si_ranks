using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader.Utils;
using SilicaAdminMod;
using UnityEngine;

namespace SiPlayerRanks
{
    // Decode once at startup; transmit on Unity's main thread through AdminMod's
    // existing non-positional, recipient-only voice stream (also works for RTS).
    internal static class RankSounds
    {
        private const int Frequency=12000, BlockSize=1024;
        private static readonly Dictionary<string,byte[][]> clips=new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<long,Playback> playing=new();
        private sealed class Playback
        {
            internal readonly Player Player;
            internal readonly Queue<byte[][]> Pending=new();
            internal byte[][] Clip;
            internal int Packet;
            internal float Next;
            internal Playback(Player player,byte[][] clip){Player=player;Clip=clip;}
        }
        internal static string Status=>"Rank sounds: "+clips.Count+" cached clips.";

        internal static void Initialize(IEnumerable<string> filenames)
        {
            Reset();clips.Clear();
            foreach(string filename in filenames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if(string.IsNullOrWhiteSpace(filename)||Path.GetFileName(filename)!=filename)
                        throw new InvalidDataException("Expected a filename inside UserData/sounds.");
                    string path=Path.Combine(MelonEnvironment.UserDataDirectory,"sounds",filename);
                    clips[filename]=Decode(File.ReadAllBytes(path));
                }
                catch(Exception error){RankLog.Warning("Rank sound disabled: "+filename+"; "+error.Message);}
            }
            RankLog.Info(Status);
        }

        internal static void Play(long id,string filename)
        {
            if(!Game.GetIsServer()||Game.GetIsClient()||!clips.TryGetValue(filename,out var clip))return;
            var player=Player.Players.FirstOrDefault(p=>p&&unchecked((long)p.PlayerID.SteamID.m_SteamID)==id);
            if(player==null||!player)return;
            if(playing.TryGetValue(id,out var playback)&&playback.Player==player)
                playback.Pending.Enqueue(clip);
            else playing[id]=new Playback(player,clip);
        }

        internal static void Tick()
        {
            if(playing.Count==0)return;
            if(!Game.GetIsServer()||Game.GetIsClient()){Reset();return;}
            float now=Time.realtimeSinceStartup;
            foreach(long id in playing.Keys.ToArray())
            {
                var playback=playing[id];
                // Never continue a clip onto another connection after a reconnect.
                if(!playback.Player||!Player.Players.Contains(playback.Player)){playing.Remove(id);continue;}
                if(now<playback.Next)continue;
                try
                {
                    AudioHelper.SendAudioPacketToPlayer(playback.Player,playback.Clip[playback.Packet],playback.Packet*BlockSize+1);
                    playback.Next=now+0.07f;
                    if(++playback.Packet==playback.Clip.Length)
                    {
                        if(playback.Pending.Count==0)playing.Remove(id);
                        else{playback.Clip=playback.Pending.Dequeue();playback.Packet=0;}
                    }
                }
                catch(Exception error)
                {playing.Remove(id);RankLog.Warning("Rank sound delivery failed: "+error.GetBaseException().Message);}
            }
        }
        internal static void Reset()=>playing.Clear();

        private static byte[][] Decode(byte[] bytes)
        {
            if(bytes.Length<12||bytes.Length>16*1024*1024||Tag(bytes,0)!="RIFF"||Tag(bytes,8)!="WAVE")
                throw new InvalidDataException("Invalid or oversized WAV file.");
            int format=0,channels=0,frequency=0,bits=0,alignment=0,data=-1,length=0;
            for(int position=12;position<=bytes.Length-8;)
            {
                int size=BitConverter.ToInt32(bytes,position+4),start=position+8;
                if(size<0||size>bytes.Length-start)throw new InvalidDataException("Truncated WAV chunk.");
                string tag=Tag(bytes,position);
                if(tag=="fmt ")
                {
                    if(size<16)throw new InvalidDataException("Invalid WAV format chunk.");
                    format=BitConverter.ToUInt16(bytes,start);channels=BitConverter.ToUInt16(bytes,start+2);
                    frequency=BitConverter.ToInt32(bytes,start+4);alignment=BitConverter.ToUInt16(bytes,start+12);
                    bits=BitConverter.ToUInt16(bytes,start+14);
                }
                else if(tag=="data"&&data<0){data=start;length=size;}
                position=checked(start+size+(size&1));
            }
            if(data<0||channels<1||channels>8||frequency<1000||frequency>192000||
               !((format==1&&(bits==8||bits==16||bits==24||bits==32))||(format==3&&bits==32))||
               alignment!=channels*(bits/8)||length==0||length%alignment!=0)
                throw new InvalidDataException("Unsupported WAV: use PCM 8/16/24/32-bit or float 32-bit.");
            int frames=length/alignment;
            if(frames/(double)frequency>30)throw new InvalidDataException("Rank cues must be at most 30 seconds.");
            var mono=new float[frames];
            for(int frame=0;frame<frames;frame++)
            {
                double sum=0;
                for(int channel=0;channel<channels;channel++)
                {
                    int offset=data+frame*alignment+channel*(bits/8);
                    double sample;
                    if(format==3)sample=BitConverter.ToSingle(bytes,offset);
                    else if(bits==8)sample=(bytes[offset]-128)/128.0;
                    else if(bits==16)sample=BitConverter.ToInt16(bytes,offset)/32768.0;
                    else if(bits==24)
                    {
                        int value=bytes[offset]|(bytes[offset+1]<<8)|(bytes[offset+2]<<16);
                        if((value&0x800000)!=0)value|=unchecked((int)0xff000000);
                        sample=value/8388608.0;
                    }
                    else sample=BitConverter.ToInt32(bytes,offset)/2147483648.0;
                    if(double.IsNaN(sample)||double.IsInfinity(sample))throw new InvalidDataException("Invalid WAV sample.");
                    sum+=sample;
                }
                mono[frame]=(float)(sum/channels);
            }
            int count=Math.Max(1,(int)(frames*(double)Frequency/frequency));
            var packets=new byte[(count+BlockSize-1)/BlockSize][];
            for(int packet=0;packet<packets.Length;packet++)
            {
                var buffer=new byte[BlockSize];
                for(int offset=0;offset<BlockSize;offset++)
                {
                    int index=packet*BlockSize+offset;
                    if(index>=count){buffer[offset]=128;continue;}
                    double source=index*(double)frequency/Frequency;
                    int left=Math.Min((int)source,frames-1),right=Math.Min(left+1,frames-1);
                    double sample=mono[left]+(mono[right]-mono[left])*(source-left);
                    buffer[offset]=(byte)Math.Max(0,Math.Min(255,(int)(sample*128+128)));
                }
                packets[packet]=buffer;
            }
            return packets;
        }
        private static string Tag(byte[] bytes,int offset)=>Encoding.ASCII.GetString(bytes,offset,4);
    }
}
