/* 
 *	Copyright (C) 2005 Team MediaPortal
 *	http://www.team-mediaportal.com
 *
 *  This Program is free software; you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation; either version 2, or (at your option)
 *  any later version.
 *   
 *  This Program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 *  GNU General Public License for more details.
 *   
 *  You should have received a copy of the GNU General Public License
 *  along with GNU Make; see the file COPYING.  If not, write to
 *  the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA. 
 *  http://www.gnu.org/copyleft/gpl.html
 *
 */
#include <windows.h>
#include "multiplexer.h"
void LogDebug(const char *fmt, ...) ;

#define MAX_PES_BUFFER_LENGTH 0x10000
#define MAX_PES_PACKET_LENGTH 0x7ec
#define PES_HEADER_LENGTH     6
#define PACK_HEADER_LENGTH    14


CMultiplexer::CMultiplexer()
{
  m_pesBuffer = new byte[MAX_PES_BUFFER_LENGTH];
	m_pCallback=NULL;
  Reset();
}

CMultiplexer::~CMultiplexer()
{
  delete [] m_pesBuffer;
  Reset();
}

void CMultiplexer::SetFileWriterCallBack(IFileWriter* callback)
{
	m_pCallback=callback;
}

void CMultiplexer::Reset()
{
	LogDebug("mux: reset");
	m_videoPacketCounter= 0;
	ivecPesDecoders it;
	for (it=m_pesDecoders.begin(); it != m_pesDecoders.end();++it)
	{
		CPesDecoder* decoder=*it;
		delete decoder;
	}
	m_pesDecoders.clear();
	m_pcrDecoder.Reset();
}

void CMultiplexer::SetPcrPid(int pcrPid)
{
	LogDebug("mux: set pcr pid:%x", pcrPid);
	m_pcrDecoder.SetPcrPid(pcrPid);
}

int CMultiplexer::GetPcrPid()
{
	return m_pcrDecoder.GetPcrPid();
}

void CMultiplexer::RemovePesStream(int pid)
{
	LogDebug("mux: remove pes pid:%x", pid);
	ivecPesDecoders it;
	it=m_pesDecoders.begin(); 
	while (it != m_pesDecoders.end())
	{
		CPesDecoder* decoder=*it;
		if (decoder->GetPid()== pid) 
		{
			delete[] decoder;
			m_pesDecoders.erase(it);
			return;
		}
		++it;
	}
}

void CMultiplexer::AddPesStream(int pid)
{
	ivecPesDecoders it;
	for (it=m_pesDecoders.begin(); it != m_pesDecoders.end();++it)
	{
		CPesDecoder* decoder=*it;
		if (decoder->GetPid()== pid) return;
	}
	
	LogDebug("mux: add pes pid:%x", pid);
	CPesDecoder* decoder = new CPesDecoder(this);
	decoder->SetPid(pid);
	decoder->SetMaxLength(0x7f2);
	m_pesDecoders.push_back(decoder);
}


void CMultiplexer::OnTsPacket(byte* tsPacket)
{
	m_pcrDecoder.OnTsPacket(tsPacket);
  
	if (m_pcrDecoder.PcrHigh()== 0 && m_pcrDecoder.PcrLow()== 0) return;
	ivecPesDecoders it;
	for (it=m_pesDecoders.begin(); it != m_pesDecoders.end();++it)
	{
		CPesDecoder* decoder=*it;
		decoder->OnTsPacket(tsPacket);
	}
}


void CMultiplexer::OnNewPesPacket(int streamId,byte* pesPacket, int pesLength, bool isStart)
{
//	LogDebug("OnNewPesPacket streamid:%x len:%x start:%d", streamId,pesLength,isStart);
	if (pesLength<=0) return;
	//is it a video stream
	if (streamId>= 0xe0 && streamId <= 0xef)
	{
		//yes
    bool writeToDisk=true;
		// is this the first video packet?
		if (m_videoPacketCounter== 0)
    {
      // yes, then start the file with a mpeg-2 sequence header...
      writeToDisk=false;
			if (isStart)
			{
				for (int x=6; x < pesLength-3;++x)
				{
					if (pesPacket[x] == 0 && pesPacket[x+1] == 0 && pesPacket[x+2] == 1 && pesPacket[x+3] == 0xb3)
					{
						writeToDisk=true;
						break;
					}
				}
			}
    }

    if (writeToDisk)
    {
		  m_videoPacketCounter++;
		  SplitPesPacket(streamId,pesPacket,pesLength,isStart);
    }
	}
	else 
	{
		//audio stream (or private stream)

		// wait for first video packet 
		// if we only have 1 stream then just write it...
		if (m_videoPacketCounter>0 || m_pesDecoders.size()== 1)
		{
			SplitPesPacket(streamId,pesPacket,pesLength,isStart);
		}
	}
}
int CMultiplexer::WritePackHeader()
{
  __int64 pcrHi=m_pcrDecoder.PcrHigh();
  int pcrLow=m_pcrDecoder.PcrLow();
  int muxRate=(6*1024*1024)/50; //6MB/s
  byte pBuffer[0x20];
	//pack header
	pBuffer[0] = 0;
	pBuffer[1] = 0;
	pBuffer[2] = 1;
	pBuffer[3] = 0xba;
	// 4             5     6         7      8       9        10        11       12       13
	//76543210  76543210 76543210 76543210 76543210 76543210 76543210 76543210 76543210 76543210
	//01PPPMPP  PPPPPPPP PPPPPMPP PPPPPPPP PPPPPMEE EEEEEEEM RRRRRRRR RRRRRRRR RRRRRRMM VVVVVSSS
	//4..9 = pcr 33bits / 9 bits
	pBuffer[4] = ((pcrHi >> 28)  & 0x3) + 4 + (((pcrHi >> 30) & 7) << 3)+ 0x40;
	pBuffer[5] = (pcrHi >> 20)  & 0xff;
	pBuffer[6] = ((pcrHi >> 13) & 0x3) + 4 + ( ( (pcrHi >> 15) & 0x1f) <<3);
	pBuffer[7] = (pcrHi >> 5) & 0xff;
	pBuffer[8] = ((pcrLow >> 7) & 3) + 4 + ((pcrHi & 0x1f)<<3);
	pBuffer[9] = ((pcrLow & 0x7f) << 1) +1 ;

	//10..12 = mux rate
	pBuffer[10] = (muxRate >> 14) & 0xff;
	pBuffer[11] = (muxRate >> 6) & 0xff;
	pBuffer[12] =((muxRate & 0x3f) << 2)+3;

	//13 pack stuffing length
	pBuffer[13] = 0xf8;
  if (m_pCallback!=NULL)
  {
	  m_pCallback->Write((byte*)pBuffer,	PACK_HEADER_LENGTH);
  }
  return PACK_HEADER_LENGTH;
}



void CMultiplexer::SplitPesPacket(int streamId,byte* pesPacket, int sectionLength,bool isStart)
{
  if (m_pCallback==NULL) return;
  WritePackHeader();
	if (sectionLength==0x7f2)
	{
    if (pesPacket[3] != streamId)
    {
      int x=1;
    }
    if (isStart!=true)
    {
      int x=1;
    }
      pesPacket[4] = 0x7;
      pesPacket[5] = 0xec;
	    m_pCallback->Write(pesPacket, sectionLength);
	}	
	else if (sectionLength==0x7e9)
	{
      m_pesBuffer[0] = 0;
      m_pesBuffer[1] = 0;
      m_pesBuffer[2] = 1;
			m_pesBuffer[3] = streamId;
      m_pesBuffer[4] = 0x7;
      m_pesBuffer[5] = 0xec;
      m_pesBuffer[6] = 0x81;
      m_pesBuffer[7] = 0;
      m_pesBuffer[8] = 0;
	    m_pCallback->Write(m_pesBuffer, 9);
	    m_pCallback->Write(pesPacket, sectionLength);
  }
  else
  {
    int written=0;
      int len=(0x7e9-sectionLength);
      if (len < 1)
      {
        int x=1;
      }
      m_pesBuffer[0] = 0;
      m_pesBuffer[1] = 0;
      m_pesBuffer[2] = 1;
			m_pesBuffer[3] = streamId;
      m_pesBuffer[4] = 0x7;
      m_pesBuffer[5] = 0xec;
      m_pesBuffer[6] = 0x81;
      m_pesBuffer[7] = 0;
      m_pesBuffer[8] = 0;
	    m_pCallback->Write(m_pesBuffer, 9);     written+=9;
	    m_pCallback->Write(pesPacket, sectionLength); written+=sectionLength;

      int userDataLen = len-6;
      m_pesBuffer[0] = 0;
      m_pesBuffer[1] = 0;
      m_pesBuffer[2] = 1;
      m_pesBuffer[3] = 0xB2;//USERDATA
      m_pesBuffer[4] = (userDataLen>>8)&0xff;
      m_pesBuffer[5] = (userDataLen)&0xff;
      memset(&m_pesBuffer[6],0xff,len);
	    m_pCallback->Write(m_pesBuffer, len);  written+=len;
      if (written!=0x7f2)
      {
        int x=1;
      }


  }
}