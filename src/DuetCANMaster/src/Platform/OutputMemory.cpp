/*
 * OutputMemory.cpp
 *
 *  Created on: 10 Jan 2016
 *      Authors: David and Christian
 */

#include "OutputMemory.h"
#include "Platform.h"
#include "RepRap.h"
#include <cstdarg>

/*static*/ OutputBuffer* volatile _ecv_null OutputBuffer::freeOutputBuffers =
	nullptr;														// Messages may also be sent by ISRs,
/*static*/ std::atomic<size_t> OutputBuffer::usedOutputBuffers = 0; // so make these atomic or volatile.
/*static*/ volatile size_t OutputBuffer::maxUsedOutputBuffers = 0;

//*************************************************************************************************
// OutputBuffer class implementation

void OutputBuffer::Append(OutputBuffer* _ecv_null other) noexcept
{
	if (other != nullptr)
	{
		m_last->m_next = other;
		OutputBuffer* const newLast = other->m_last;
		if (other->m_hadOverflow)
		{
			m_hadOverflow = true;
		}

		OutputBuffer* item = this;
		do
		{
			item->m_last = newLast;
			item = item->Next();
		} while (item != other);
	}
}

void OutputBuffer::IncreaseReferences(size_t refs) noexcept
{
	if (refs != 0)
	{
		const TaskCriticalSectionLocker lock;

		for (OutputBuffer* item = this; item != nullptr; item = item->Next())
		{
			item->m_references += refs;
			item->m_isReferenced = true;
		}
	}
}

size_t OutputBuffer::Length() const noexcept
{
	size_t totalLength = 0;
	for (const OutputBuffer* current = this; current != nullptr; current = current->Next())
	{
		totalLength += current->DataLength();
	}
	return totalLength;
}

char OutputBuffer::operator[](size_t index) const noexcept
{
	// Get the right buffer to access
	const OutputBuffer* itemToIndex = this;
	while (index >= itemToIndex->DataLength())
	{
		index -= itemToIndex->DataLength();
		itemToIndex = itemToIndex->Next();
	}

	// Return the char reference
	return itemToIndex->m_data[index];
}

const char* _ecv_array OutputBuffer::Read(size_t len) noexcept
{
	const size_t offset = m_bytesRead;
	m_bytesRead += len;
	return m_data + offset;
}

// Empty this buffer
void OutputBuffer::Clear() noexcept
{
	if (m_next != nullptr)
	{
		ReleaseAll(m_next);
		m_last = this;
	}
	m_dataLength = 0;
}

void OutputBuffer::UpdateWhenQueued() noexcept
{
	m_whenQueued = millis();
}

size_t OutputBuffer::Vprintf(const char* _ecv_array fmt, va_list vargs) noexcept
{
	Clear();
	return Vcatf(fmt, vargs);
}

size_t OutputBuffer::Printf(const char* _ecv_array fmt, ...) noexcept
{
	va_list vargs;
	va_start(vargs, fmt);
	const size_t ret = Vprintf(fmt, vargs);
	va_end(vargs);
	return ret;
}

size_t OutputBuffer::Vcatf(const char* _ecv_array fmt, va_list vargs) noexcept
{
	return vuprintf([this](char c) noexcept -> bool { return c != 0 && Cat(c) != 0; }, fmt, vargs);
}

size_t OutputBuffer::Catf(const char* _ecv_array fmt, ...) noexcept
{
	va_list vargs;
	va_start(vargs, fmt);
	const size_t ret = Vcatf(fmt, vargs);
	va_end(vargs);
	return ret;
}

size_t OutputBuffer::Lcatf(const char* _ecv_array fmt, ...) noexcept
{
	size_t extra = 0;
	if (m_last->m_dataLength != 0 && m_last->m_data[m_last->m_dataLength - 1] != '\n')
	{
		extra = Cat('\n');
		if (extra == 0)
		{
			return 0;
		}
	}

	va_list vargs;
	va_start(vargs, fmt);
	const size_t ret = Vcatf(fmt, vargs);
	va_end(vargs);
	return ret + extra;
}

size_t OutputBuffer::Copy(const char c) noexcept
{
	Clear();
	m_data[0] = c;
	m_dataLength = 1;
	return 1;
}

size_t OutputBuffer::Copy(const char* _ecv_array src) noexcept
{
	return Copy(src, strlen(src));
}

size_t OutputBuffer::Copy(const char* _ecv_array src, size_t len) noexcept
{
	Clear();
	return Cat(src, len);
}

size_t OutputBuffer::Cat(const char c) noexcept
{
	// See if we can append a char
	if (m_last->m_dataLength == OUTPUT_BUFFER_SIZE)
	{
		// No - allocate a new item and copy the data
		OutputBuffer* nextBuffer = nullptr;
		if (!Allocate(nextBuffer, false))
		{
			// We cannot store any more data
			m_hadOverflow = true;
			return 0;
		}
		nextBuffer->m_references = m_references.load();
		nextBuffer->Copy(c);

		// Link the new item to this list
		m_last->m_next = nextBuffer;
		for (OutputBuffer* item = this; item != nextBuffer; item = item->Next())
		{
			item->m_last = nextBuffer;
		}
	}
	else
	{
		// Yes - we have enough space left
		m_last->m_data[m_last->m_dataLength++] = c;
	}
	return 1;
}

size_t OutputBuffer::Cat(const char* _ecv_array src) noexcept
{
	return Cat(src, strlen(src));
}

size_t OutputBuffer::Lcat(const char* _ecv_array src) noexcept
{
	return Lcat(src, strlen(src));
}

size_t OutputBuffer::Cat(const char* _ecv_array src, size_t len) noexcept
{
	size_t copied = 0;
	while (copied < len)
	{
		if (m_last->m_dataLength == OUTPUT_BUFFER_SIZE)
		{
			// Save at least some output buffers in case this buffer chain has to be sent via network.
			// If we don't do this, the network responder may be unable to allocate enough for the header
			OutputBuffer* nextBuffer = nullptr;
			if (!Allocate(nextBuffer, false))
			{
				// We cannot store any more data, stop here
				m_hadOverflow = true;
				break;
			}
			nextBuffer->m_references = m_references.load();
			m_last->m_next = nextBuffer;
			OutputBuffer* item = this;
			do
			{
				item->m_last = nextBuffer;
				item = item->Next();
			} while (item != nextBuffer);
		}
		const auto copyLength = min<size_t>(len - copied, OUTPUT_BUFFER_SIZE - m_last->m_dataLength);
		memcpy(m_last->m_data + m_last->m_dataLength, src + copied, copyLength);
		m_last->m_dataLength += copyLength;
		copied += copyLength;
	}
	return copied;
}

size_t OutputBuffer::Lcat(const char* _ecv_array src, size_t len) noexcept
{
	size_t extra = 0;
	if (m_last->m_dataLength != 0 && m_last->m_data[m_last->m_dataLength - 1] != '\n')
	{
		extra = Cat('\n');
		if (extra == 0)
		{
			return 0;
		}
	}

	return Cat(src, len) + extra;
}

size_t OutputBuffer::Cat(StringRef& str) noexcept
{
	return Cat(str.c_str(), str.strlen());
}

// Encode a character in JSON format, and append it to the buffer and return the number of bytes written
size_t OutputBuffer::EncodeChar(char c) noexcept
{
	char esc = 0;
	switch (c)
	{
	case '\r':
		esc = 'r';
		break;
	case '\n':
		esc = 'n';
		break;
	case '\t':
		esc = 't';
		break;
	case '"':
	case '\\':
#if 1
		// Escaping '/' is optional in JSON, although doing so so confuses PanelDue (fixed in PanelDue firmware
		// version 1.15 and later). As it's optional, we don't do it.
#else
	case '/':
#endif
		esc = c;
		break;
	default:
		esc = 0;
		break;
	}

	if (esc != 0)
	{
		const size_t written = Cat('\\');
		return (written == 0) ? written : written + Cat(esc);
	}

	return Cat(c);
}

size_t OutputBuffer::EncodeReply(OutputBuffer* _ecv_null src) noexcept
{
	size_t bytesWritten = Cat('"');

	while (src != nullptr)
	{
		for (size_t index = 0; index < src->DataLength(); ++index)
		{
			bytesWritten += EncodeChar(src->Data()[index]);
		}
		src = Release(src);
	}

	bytesWritten += Cat('"');
	return bytesWritten;
}

// Initialise the output buffers manager
/*static*/ void OutputBuffer::Init() noexcept
{
	freeOutputBuffers = nullptr;
	for (size_t i = 0; i < OUTPUT_BUFFER_COUNT; i++)
	{
		freeOutputBuffers = new OutputBuffer(freeOutputBuffers);
	}
}

// Allocates an output buffer instance which can be used for (large) string outputs. This must be thread safe. Not safe
// to call from interrupts!
/*static*/ bool OutputBuffer::Allocate(OutputBuffer* _ecv_null& buf, bool allowReserved) noexcept
{
	{
		const TaskCriticalSectionLocker lock;

		buf = freeOutputBuffers;
		if (buf != nullptr && (allowReserved || OUTPUT_BUFFER_COUNT - usedOutputBuffers > RESERVED_OUTPUT_BUFFERS))
		{
			freeOutputBuffers = buf->m_next;
			usedOutputBuffers++;
			maxUsedOutputBuffers = std::max<std::atomic<size_t>>(usedOutputBuffers, maxUsedOutputBuffers);

			// Initialise the buffer before we release the lock in case another task uses it immediately
			buf->m_next = nullptr;
			buf->m_last = buf;
			buf->m_dataLength = buf->m_bytesRead = 0;
			buf->m_references = 1; // assume it's only used once by default
			buf->m_isReferenced = false;
			buf->m_hadOverflow = false;
			buf->UpdateWhenQueued(); // use the time of allocation as the default when-used time

			return true;
		}
	}

	reprap.GetPlatform().LogError(ErrorCode::OutputStarvation);
	return false;
}

// Get the number of bytes left for continuous writing
/*static*/ size_t OutputBuffer::GetBytesLeft(const OutputBuffer* writingBuffer) noexcept
{
	const size_t freeBuffers = OUTPUT_BUFFER_COUNT - usedOutputBuffers;
	const size_t bytesLeft = OUTPUT_BUFFER_SIZE - writingBuffer->m_last->DataLength();

	if (freeBuffers < RESERVED_OUTPUT_BUFFERS)
	{
		// Keep some space left to encapsulate the responses (e.g. via an HTTP header)
		return bytesLeft;
	}

	return bytesLeft + (freeBuffers - RESERVED_OUTPUT_BUFFERS) * OUTPUT_BUFFER_SIZE;
}

// Truncate an output buffer to free up more memory. Returns the number of released bytes.
// This never releases the first buffer in the chain, so call it with a large value of bytesNeeded to release all
// buffers except the first.
/*static */ size_t OutputBuffer::Truncate(OutputBuffer* _ecv_null buffer, size_t bytesNeeded) noexcept
{
	// Can we free up space from this chain? Don't break it up if it's referenced anywhere else
	if (buffer == nullptr || buffer->Next() == nullptr || buffer->IsReferenced())
	{
		// No
		return 0;
	}

	// Yes - free up the last entries
	size_t releasedBytes = 0;
	OutputBuffer* previousItem = nullptr;
	do
	{
		// Get two the last entries from the chain
		previousItem = buffer;
		OutputBuffer* lastItem = previousItem->Next();
		while (lastItem->Next() != nullptr)
		{
			previousItem = lastItem;
			lastItem = lastItem->Next();
		}

		// Unlink and free the last entry
		ReleaseAll(previousItem->m_next);
		releasedBytes += OUTPUT_BUFFER_SIZE;
	} while (previousItem != buffer && releasedBytes < bytesNeeded);

	// Update all the references to the last item
	for (OutputBuffer* _ecv_null item = buffer; item != nullptr; item = item->Next())
	{
		item->m_last = previousItem;
	}
	return releasedBytes;
}

// Releases an output buffer instance and returns the next entry from the chain
/*static */ OutputBuffer* OutputBuffer::Release(OutputBuffer* buf) noexcept
{
	const TaskCriticalSectionLocker lock;
	OutputBuffer* const nextBuffer = buf->m_next;

	// If this one is reused by another piece of code, don't free it up
	if (buf->m_references > 1)
	{
		buf->m_references--;
		buf->m_bytesRead = 0;
	}
	else
	{
		// Otherwise prepend it to the list of free output buffers again
		buf->m_next = freeOutputBuffers;
		freeOutputBuffers = buf;
		usedOutputBuffers--;
	}
	return nextBuffer;
}

/*static */ void OutputBuffer::ReleaseAll(OutputBuffer* volatile _ecv_null& buf) noexcept
{
	while (buf != nullptr)
	{
		buf = Release(buf);
	}
}

/*static*/ void OutputBuffer::Diagnostics(const StringRef& reply) noexcept
{
	reply.lcatf(
		"Used output buffers: %d of %d (%d max)", usedOutputBuffers.load(), OUTPUT_BUFFER_COUNT, maxUsedOutputBuffers);
}

//*************************************************************************************************
// OutputStack class implementation

// Push an OutputBuffer chain. Return true if successful, else release the buffer and return false.
bool OutputStack::Push(OutputBuffer* _ecv_null buffer, MessageType type) volatile noexcept
{
	{
		const TaskCriticalSectionLocker lock;

		if (m_count < OUTPUT_STACK_DEPTH)
		{
			if (buffer != nullptr)
			{
				buffer->UpdateWhenQueued();
			}
			m_items[m_count] = buffer;
			m_types[m_count] = type;
			// enclosing method is volatile and this counter is updated non-atomically on purpose
			// NOLINTNEXTLINE(cppcoreguidelines-pro-type-const-cast) - drops volatile, not const: the
			const_cast<OutputStack*>(this)->m_count++;
			return true;
		}
	}
	OutputBuffer::ReleaseAll(buffer);
	reprap.GetPlatform().LogError(ErrorCode::OutputStackOverflow);
	return false;
}

// Pop an OutputBuffer chain or return nullptr if none is available
OutputBuffer* OutputStack::Pop() volatile noexcept
{
	const TaskCriticalSectionLocker lock;

	if (m_count == 0)
	{
		return nullptr;
	}

	OutputBuffer* item = m_items[0];
	for (size_t i = 1; i < m_count; i++)
	{
		m_items[i - 1] = m_items[i];
		m_types[i - 1] = m_types[i];
	}
	// NOLINTNEXTLINE(cppcoreguidelines-pro-type-const-cast) - drops volatile, not const
	const_cast<OutputStack*>(this)->m_count--;

	return item;
}

// Returns the first item from the stack or nullptr if none is available
OutputBuffer* OutputStack::GetFirstItem() const volatile noexcept
{
	return (m_count == 0) ? nullptr : m_items[0];
}

// Returns the first item's type from the stack or NoDestinationMessage if none is available
MessageType OutputStack::GetFirstItemType() const volatile noexcept
{
	return (m_count == 0) ? MessageType::NoDestinationMessage : m_types[0];
}

#if HAS_SBC_INTERFACE

// Update the first item of the stack
void OutputStack::SetFirstItem(OutputBuffer* _ecv_null buffer) volatile noexcept
{
	if (m_count != 0)
	{
		if (buffer == nullptr)
		{
			(void)Pop();
		}
		else
		{
			m_items[0] = buffer;
			buffer->UpdateWhenQueued();
		}
	}
}

#endif

// Release the first item at the top of the stack
void OutputStack::ReleaseFirstItem() volatile noexcept
{
	if (m_count != 0)
	{
		OutputBuffer* const buf = m_items[0]; // capture volatile variable
		if (buf != nullptr)
		{
			m_items[0] = OutputBuffer::Release(buf);
		}
		if (m_items[0] == nullptr)
		{
			(void)Pop();
		}
	}
}

// Release the first item on the top of the stack if it is too old. Return true if the item was timed out or was null.
bool OutputStack::ApplyTimeout(uint32_t ticks) volatile noexcept
{
	bool ret = false;
	if (m_count != 0)
	{
		OutputBuffer* buf = m_items[0]; // capture volatile variable
		while (buf != nullptr && millis() - buf->WhenQueued() >= ticks)
		{
			m_items[0] = buf = OutputBuffer::Release(buf);
			ret = true;
		}
		if (m_items[0] == nullptr)
		{
			(void)Pop();
			ret = true;
		}
	}
	return ret;
}

// Returns the last item from the stack or nullptr if none is available
OutputBuffer* _ecv_null OutputStack::GetLastItem() const volatile noexcept
{
	return (m_count == 0) ? nullptr : m_items[m_count - 1];
}

// Returns the type of the last item from the stack or NoDestinationMessage if none is available
MessageType OutputStack::GetLastItemType() const volatile noexcept
{
	return (m_count == 0) ? MessageType::NoDestinationMessage : m_types[m_count - 1];
}

// Get the total length of all queued buffers
size_t OutputStack::DataLength() const volatile noexcept
{
	size_t totalLength = 0;

	const TaskCriticalSectionLocker lock;
	for (size_t i = 0; i < m_count; i++)
	{
		if (m_items[i] != nullptr)
		{
			totalLength += m_items[i]->Length();
		}
	}

	return totalLength;
}

// Append another OutputStack to this instance. If no more space is available,
// all OutputBuffers that can't be added are automatically released
void OutputStack::Append(volatile OutputStack& stack) volatile noexcept
{
	for (size_t i = 0; i < stack.m_count; i++)
	{
		if (m_count < OUTPUT_STACK_DEPTH)
		{
			m_items[m_count] = stack.m_items[i];
			m_types[m_count] = stack.m_types[i];
			// enclosing method is volatile and this counter is updated non-atomically on purpose
			// NOLINTNEXTLINE(cppcoreguidelines-pro-type-const-cast) - drops volatile, not const: the
			const_cast<OutputStack*>(this)->m_count++;
		}
		else
		{
			reprap.GetPlatform().LogError(ErrorCode::OutputStackOverflow);
			OutputBuffer::ReleaseAll(stack.m_items[i]);
		}
	}
}

// Increase the number of references for each OutputBuffer on the stack
void OutputStack::IncreaseReferences(size_t num) volatile noexcept
{
	const TaskCriticalSectionLocker lock;
	for (size_t i = 0; i < m_count; i++)
	{
		if (m_items[i] != nullptr)
		{
			m_items[i]->IncreaseReferences(num);
		}
	}
}

// Release all buffers and clean up
void OutputStack::ReleaseAll() volatile noexcept
{
	for (size_t i = 0; i < m_count; i++)
	{
		OutputBuffer::ReleaseAll(m_items[i]);
	}
	m_count = 0;
}

// End
